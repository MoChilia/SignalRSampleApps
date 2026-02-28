# coding=utf-8
# --------------------------------------------------------------------------
# Copyright (c) Microsoft Corporation. All rights reserved.
# Licensed under the MIT License. See License.txt in the project root for license information.
# --------------------------------------------------------------------------
"""
Upstream event handler server for the invoke_event.py sample.

This Flask server handles CloudEvents HTTP requests sent by Azure Web PubSub,
including:
  - Abuse protection (OPTIONS webhook validation)
  - System "connect" event
  - Custom user events: "processOrder" and "echo"

Usage:
  1. pip install flask
  2. python invoke_event_server.py          (starts on http://0.0.0.0:8080)
  3. Configure your Web PubSub hub "chat" with upstream URL:
       http://<your-public-host>:8080/eventhandler
     Event handlers should include system events (connect, connected, disconnected)
     and user events (processOrder, echo) — or use "*" to match all user events.
  4. Run invoke_event.py in another terminal.
"""

import json
import sys
import time
from flask import Flask, request, Response, jsonify

app = Flask(__name__)


@app.route("/eventhandler", methods=["OPTIONS"])
def handle_abuse_protection():
    """Respond to CloudEvents abuse-protection handshake."""
    origin = request.headers.get("WebHook-Request-Origin", "")
    print(f"[abuse-protection] origin={origin}")
    resp = Response(status=200)
    resp.headers["WebHook-Allowed-Origin"] = "*"
    return resp


@app.route("/eventhandler", methods=["POST"])
def handle_event():
    """Dispatch incoming CloudEvents to the appropriate handler."""
    ce_type = request.headers.get("ce-type", "")
    ce_event = request.headers.get("ce-eventName", "")
    ce_user = request.headers.get("ce-userId", "")
    ce_conn = request.headers.get("ce-connectionId", "")
    content_type = request.content_type or ""

    print(f"[event] ce-type={ce_type}  ce-eventName={ce_event}  "
          f"ce-userId={ce_user}  ce-connectionId={ce_conn}  "
          f"Content-Type={content_type}")

    # ── System: connect ──────────────────────────────────────────────
    if ce_type == "azure.webpubsub.sys.connect":
        return handle_connect()

    # ── System: connected / disconnected ─────────────────────────────
    if ce_type in ("azure.webpubsub.sys.connected",
                   "azure.webpubsub.sys.disconnected"):
        print(f"[{ce_event}] connectionId={ce_conn}")
        return Response(status=200)

    # ── User custom events (used by both "event" and "invoke" flows) ─
    if ce_type.startswith("azure.webpubsub.user."):
        return handle_user_event(ce_event, content_type)

    # Unknown event type
    print(f"[unknown] ce-type={ce_type}")
    return Response(status=400)


# ─── Handlers ────────────────────────────────────────────────────────────

def handle_connect():
    """
    Accept the WebSocket connection.
    Return 200 with an empty JSON body so the service keeps the connection alive.
    """
    body = request.get_json(silent=True) or {}
    print(f"[connect] claims={body.get('claims', {})}")
    return jsonify({}), 200


def handle_user_event(event_name: str, content_type: str):
    """
    Handle user custom events forwarded by Web PubSub.

    For an invoke flow the service converts this HTTP response into an
    ``invokeResponse`` message that is delivered back to the calling client.

    Response rules (per CloudEvents spec):
      - Content-Type: application/json  → client receives dataType=json
      - Content-Type: text/plain        → client receives dataType=text
      - Content-Type: application/octet-stream → client receives dataType=binary
      - HTTP 4xx / 5xx                 → client receives invokeResponse with success=false
    """
    raw = request.get_data(as_text=True)
    print(f"[user-event:{event_name}] body={raw[:200]}")

    if event_name == "processOrder":
        return _handle_process_order(content_type, raw)
    elif event_name == "echo":
        return _handle_echo(content_type, raw)
    elif event_name == "slowEvent":
        return _handle_slow_event(content_type, raw)
    else:
        # Generic passthrough — echo back the payload with its original type
        return Response(raw, status=200, content_type=content_type)


def _handle_process_order(content_type: str, raw: str):
    """Process the 'processOrder' invoke event and return a JSON result."""
    try:
        payload = json.loads(raw) if raw else {}
    except json.JSONDecodeError:
        payload = {}

    order_id = payload.get("orderId", "unknown")
    print(payload)
    result = {
        "status": "completed",
        "orderId": order_id,
        "message": f"Order {order_id} processed successfully",
    }
    print(f"[processOrder] orderId={order_id} → result={result}")
    return jsonify(result), 200


def _handle_echo(content_type: str, raw: str):
    """Echo back whatever the client sent, matching the original content type."""
    print(f"[echo] echoing back: {raw[:200]}")

    if "application/json" in content_type:
        return Response(raw, status=200, content_type="application/json")
    elif "text/plain" in content_type:
        return Response(raw, status=200, content_type="text/plain")
    else:
        return Response(raw, status=200, content_type="application/octet-stream")


def _handle_slow_event(content_type: str, raw: str):
    """Simulate a slow upstream handler that takes 30 seconds to respond.

    Use this with a short client timeout to test cancel invocation.
    """
    try:
        payload = json.loads(raw) if raw else {}
    except json.JSONDecodeError:
        payload = {}

    delay = payload.get("delay", 30)
    print(f"[slowEvent] sleeping for {delay}s ...")
    time.sleep(delay)
    print(f"[slowEvent] done sleeping")
    return jsonify({"status": "completed", "delayed": delay}), 200


# ─── Entry point ─────────────────────────────────────────────────────────

if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8080
    print(f"Starting upstream event handler on http://0.0.0.0:{port}/eventhandler")
    app.run(host="0.0.0.0", port=port, debug=True)
