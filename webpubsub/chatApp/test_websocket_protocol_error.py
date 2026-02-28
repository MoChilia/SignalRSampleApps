#!/usr/bin/env python3
"""
Test script to trigger WebSocketProtocolError against local SignalR/WebPubSub runtime.
Requires: pip install websockets azure-messaging-webpubsubservice python-dotenv

Usage:
    python test_websocket_protocol_error.py <test_name>
    
Tests:
    continuation_from_final - Send continuation frame after final message
    invalid_utf8_text      - Send text frame with invalid UTF-8
    invalid_close_length   - Send close frame with 1-byte payload
    invalid_close_code     - Send close frame with invalid status code
    invalid_close_desc     - Send close frame with non-UTF-8 description
    reserved_bits          - Send frame with reserved bits set
    closed_prematurely     - Kill TCP socket without close handshake

NOTE: The detailed UserFriendlyMessage is visible in:
  1. Server console logs ("Error when dispatching payload" / "Connection ended")
  2. WebPubSub subprotocol disconnect message (use --subprotocol flag)

The WebSocket close frame only carries status code 1002 with no description.
"""

import asyncio
import json
import os
import struct
import sys

from dotenv import load_dotenv
from azure.messaging.webpubsubservice import WebPubSubServiceClient

load_dotenv()

# Configuration - use local WebPubSubConnectionString to get an authenticated URL
connection_string = os.environ.get('WebPubSubConnectionString')
hub_name = 'chat'
service = WebPubSubServiceClient.from_connection_string(connection_string, hub=hub_name)

USE_SUBPROTOCOL = '--subprotocol' in sys.argv
SUBPROTOCOL = 'json.webpubsub.azure.v1'


def get_ws_url():
    """Get WebSocket URL, optionally requesting subprotocol roles."""
    token = service.get_client_access_token()
    return token['url']


def build_websocket_frame(opcode: int, payload: bytes, fin: bool = True, mask: bool = True) -> bytes:
    """Build a raw WebSocket frame with optional masking."""
    frame = bytearray()
    
    # First byte: FIN + opcode
    first_byte = (0x80 if fin else 0x00) | (opcode & 0x0F)
    frame.append(first_byte)
    
    # Second byte: MASK + payload length
    mask_bit = 0x80 if mask else 0x00
    payload_len = len(payload)
    
    if payload_len <= 125:
        frame.append(mask_bit | payload_len)
    elif payload_len <= 65535:
        frame.append(mask_bit | 126)
        frame.extend(struct.pack(">H", payload_len))
    else:
        frame.append(mask_bit | 127)
        frame.extend(struct.pack(">Q", payload_len))
    
    # Masking key (required for client-to-server)
    if mask:
        mask_key = b'\x12\x34\x56\x78'
        frame.extend(mask_key)
        # XOR payload with mask
        masked_payload = bytes(b ^ mask_key[i % 4] for i, b in enumerate(payload))
        frame.extend(masked_payload)
    else:
        frame.extend(payload)
    
    return bytes(frame)


async def get_raw_connection(url: str):
    """Establish WebSocket connection and return the websocket object."""
    import websockets
    subprotocols = [SUBPROTOCOL] if USE_SUBPROTOCOL else []
    ws = await websockets.connect(url, subprotocols=subprotocols)
    if USE_SUBPROTOCOL:
        # Read the "connected" system message
        connected_msg = await asyncio.wait_for(ws.recv(), timeout=5.0)
        print(f"  Connected message: {connected_msg}")
    return ws


async def recv_and_print(ws):
    """
    Try to receive messages until the connection closes.
    With subprotocol, we may get a 'disconnected' message containing UserFriendlyMessage.
    Without subprotocol, we only see the close frame code.
    """
    try:
        while True:
            msg = await asyncio.wait_for(ws.recv(), timeout=5.0)
            # Try to parse as JSON to pretty-print
            try:
                parsed = json.loads(msg)
                if parsed.get('type') == 'system' and parsed.get('event') == 'disconnected':
                    print(f"  >>> Disconnected message (contains UserFriendlyMessage):")
                    print(f"      {json.dumps(parsed, indent=6)}")
                else:
                    print(f"  Received: {msg}")
            except (json.JSONDecodeError, TypeError):
                print(f"  Received: {msg}")
    except Exception as e:
        print(f"  Connection closed: {type(e).__name__}: {e}")
    
    # Print close frame details
    print(f"  Close code: {ws.close_code}")
    print(f"  Close reason: '{ws.close_reason}'")


async def test_continuation_from_final(url: str):
    """
    Send a continuation frame as the FIRST frame.
    The state machine starts with _lastReceiveHeader.Fin = true,
    so any continuation frame is immediately invalid.
    """
    print("Test: Continuation frame after final message")
    
    ws = await get_raw_connection(url)
    transport = ws.transport
    
    # Send continuation frame IMMEDIATELY (no valid message first!)
    # The WebSocket state starts with Fin=true, so this is invalid
    continuation_frame = build_websocket_frame(
        opcode=0x00,  # Continuation
        payload=b"World",
        fin=True
    )
    transport.write(continuation_frame)
    
    print("  Sent malformed continuation frame...")
    await recv_and_print(ws)


async def test_invalid_utf8_text(url: str):
    """
    Send a text frame with invalid UTF-8 bytes.
    Expected error: "The WebSocket received a text message with invalid UTF-8 payload data."
    """
    print("Test: Text frame with invalid UTF-8")
    
    ws = await get_raw_connection(url)
    transport = ws.transport
    
    # Invalid UTF-8 sequence: 0xFF 0xFE are not valid UTF-8 lead bytes
    invalid_utf8 = b'\xFF\xFE\x00\x01'
    
    text_frame = build_websocket_frame(
        opcode=0x01,  # Text
        payload=invalid_utf8,
        fin=True
    )
    transport.write(text_frame)
    
    print("  Sent text frame with invalid UTF-8...")
    await recv_and_print(ws)


async def test_invalid_close_length(url: str):
    """
    Send a close frame with exactly 1 byte payload.
    Close payload must be 0 bytes OR >= 2 bytes (2-byte status code + optional reason).
    Expected error: "The WebSocket received a close frame with an invalid payload length of 1."
    """
    print("Test: Close frame with 1-byte payload")
    
    ws = await get_raw_connection(url)
    transport = ws.transport
    
    # Close frame with 1 byte - invalid!
    close_frame = build_websocket_frame(
        opcode=0x08,  # Close
        payload=b'\x03',  # Just 1 byte - invalid
        fin=True
    )
    transport.write(close_frame)
    
    print("  Sent close frame with 1-byte payload...")
    await recv_and_print(ws)


async def test_invalid_close_code(url: str):
    """
    Send a close frame with an invalid/reserved close status code.
    Codes 0-999 and some in 1000-2999 range are reserved/invalid.
    Expected error: "The WebSocket received a close frame with an invalid close status code."
    """
    print("Test: Close frame with invalid status code")
    
    ws = await get_raw_connection(url)
    transport = ws.transport
    
    # Status code 999 is "not used" per RFC 6455
    invalid_code = struct.pack(">H", 999)
    
    close_frame = build_websocket_frame(
        opcode=0x08,  # Close
        payload=invalid_code,
        fin=True
    )
    transport.write(close_frame)
    
    print("  Sent close frame with invalid status code (999)...")
    await recv_and_print(ws)


async def test_invalid_close_description(url: str):
    """
    Send a close frame with valid status code but non-UTF-8 reason description.
    Expected error: "The WebSocket received a close frame with a description that is not valid UTF-8."
    """
    print("Test: Close frame with non-UTF-8 description")
    
    ws = await get_raw_connection(url)
    transport = ws.transport
    
    # Valid close code (1000 = normal closure) + invalid UTF-8 description
    status_code = struct.pack(">H", 1000)
    invalid_description = b'\xFF\xFE'  # Not valid UTF-8
    
    close_frame = build_websocket_frame(
        opcode=0x08,  # Close
        payload=status_code + invalid_description,
        fin=True
    )
    transport.write(close_frame)
    
    print("  Sent close frame with non-UTF-8 description...")
    await recv_and_print(ws)


async def test_reserved_bits(url: str):
    """
    Send a frame with reserved bits set (RSV1, RSV2, or RSV3).
    Expected error: "The WebSocket received a frame with one or more reserved bits set."
    """
    print("Test: Frame with reserved bits set")
    
    ws = await get_raw_connection(url)
    transport = ws.transport
    
    # Build frame manually with RSV1 bit set (0x40)
    frame = bytearray()
    frame.append(0x81 | 0x40)  # FIN + RSV1 + Text opcode
    payload = b"Hello"
    mask_key = b'\x12\x34\x56\x78'
    frame.append(0x80 | len(payload))  # MASK + length
    frame.extend(mask_key)
    masked = bytes(b ^ mask_key[i % 4] for i, b in enumerate(payload))
    frame.extend(masked)
    
    transport.write(bytes(frame))
    
    print("  Sent frame with RSV1 bit set...")
    await recv_and_print(ws)


async def test_client_closed_prematurely(url: str):
    """
    Abruptly kill the TCP socket without sending a WebSocket close frame.
    This simulates browser tab close, network drop, or client crash.
    Expected: Server sees WebSocketException(ConnectionClosedPrematurely)
              classified as Normal (not ServiceTransientError).
    """
    print("Test: Client connection closed prematurely (no close handshake)")

    ws = await get_raw_connection(url)
    transport = ws.transport

    # Small delay to let the connection fully establish on the server side
    await asyncio.sleep(0.5)

    # Abruptly kill the underlying TCP transport — no close frame sent
    transport.close()
    print("  Killed TCP socket without close handshake.")
    print("  Server should log ConnectionClosedPrematurely with category=Normal")


TESTS = {
    "continuation_from_final": test_continuation_from_final,
    "invalid_utf8_text": test_invalid_utf8_text,
    "invalid_close_length": test_invalid_close_length,
    "invalid_close_code": test_invalid_close_code,
    "invalid_close_desc": test_invalid_close_description,
    "reserved_bits": test_reserved_bits,
    "closed_prematurely": test_client_closed_prematurely,
}


async def main():
    if len(sys.argv) < 2:
        print(__doc__)
        print(f"Available tests: {', '.join(TESTS.keys())}")
        print(f"\nFlags:")
        print(f"  --subprotocol  Use json.webpubsub.azure.v1 subprotocol to receive disconnect message")
        print(f"\nExamples:")
        print(f"  python test_websocket_protocol_error.py continuation_from_final")
        print(f"  python test_websocket_protocol_error.py all --subprotocol")
        return
    
    # Filter out flags from args
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    test_name = args[0] if args else 'all'
    url = get_ws_url()
    
    print(f"Mode: {'subprotocol' if USE_SUBPROTOCOL else 'simple WebSocket'}")
    print(f"Tip: Check server console logs for the full UserFriendlyMessage\n")
    
    if test_name == "all":
        for name, test_fn in TESTS.items():
            print(f"\n{'='*60}")
            try:
                await test_fn(url)
            except Exception as e:
                print(f"Test failed with: {e}")
            print()
    elif test_name in TESTS:
        await TESTS[test_name](url)
    else:
        print(f"Unknown test: {test_name}")
        print(f"Available tests: {', '.join(TESTS.keys())}, all")


if __name__ == "__main__":
    asyncio.run(main())