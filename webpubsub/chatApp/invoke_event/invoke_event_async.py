# coding=utf-8
# --------------------------------------------------------------------------
# Copyright (c) Microsoft Corporation. All rights reserved.
# Licensed under the MIT License. See License.txt in the project root for license information.
# --------------------------------------------------------------------------
import os
import logging
import asyncio
from azure.messaging.webpubsubclient.aio import WebPubSubClient, WebPubSubClientCredential
from azure.messaging.webpubsubservice.aio import WebPubSubServiceClient
from azure.messaging.webpubsubclient.models import (
    OnConnectedArgs,
    OnDisconnectedArgs,
    CallbackType,
    WebPubSubDataType,
    InvocationError,
)
from dotenv import load_dotenv

load_dotenv()

# Enable DEBUG to see raw WebSocket messages (including invokeResponse)
logging.basicConfig(level=logging.DEBUG)

async def on_connected(msg: OnConnectedArgs):
    print("======== connected ===========")
    print(f"Connection {msg.connection_id} is connected")


async def on_disconnected(msg: OnDisconnectedArgs):
    print("========== disconnected =========")
    print(f"connection is disconnected: {msg.message}")


async def main():
    service_client = WebPubSubServiceClient.from_connection_string(  # type: ignore
        connection_string=os.getenv("WEBPUBSUB_CONNECTION_STRING", ""), hub="chat"
    )

    async def client_access_url_provider():
        return (await service_client.get_client_access_token(
            roles=["webpubsub.sendToGroup", "webpubsub.joinLeaveGroup"]
        ))["url"]

    client = WebPubSubClient(
        credential=WebPubSubClientCredential(client_access_url_provider=client_access_url_provider),
    )

    async with client:
        await client.subscribe(CallbackType.CONNECTED, on_connected)
        await client.subscribe(CallbackType.DISCONNECTED, on_disconnected)

        # Invoke an upstream event with JSON data
        try:
            result = await client.invoke_event(
                "processOrder", {"orderId": 1}, WebPubSubDataType.JSON, timeout=60.0
            )
            print("=== invokeResponse (processOrder) ===")
            print(f"  invocation_id : {result.invocation_id}")
            print(f"  data_type     : {result.data_type}")
            print(f"  data          : {result.data}")
        except InvocationError as e:
            print(f"Invocation failed: {e.message}")
            if e.error_detail:
                print(f"  error name: {e.error_detail.name}, message: {e.error_detail.message}")

        # Invoke an upstream event with text data (default timeout)
        try:
            result = await client.invoke_event("echo", "hello", WebPubSubDataType.TEXT)
            print("=== invokeResponse (echo) ===")
            print(f"  invocation_id : {result.invocation_id}")
            print(f"  data_type     : {result.data_type}")
            print(f"  data          : {result.data}")
        except InvocationError as e:
            print(f"Echo failed: {e.message}")

        # Test cancel invocation: invoke a slow event with a short timeout.
        # The client will time out and automatically send a cancelInvocation
        # message to the server.
        try:
            print("\n=== invokeEvent (slowEvent) — expecting timeout & cancel ===")
            result = await client.invoke_event(
                "slowEvent",
                {"delay": 30},
                WebPubSubDataType.JSON,
                timeout=3.0,  # short timeout triggers cancellation
            )
            # Should not reach here
            print(f"  Unexpected success: {result.data}")
        except InvocationError as e:
            print(f"  Caught expected InvocationError: {e.message}")
            print(f"  invocation_id: {e.invocation_id}")
            print(f"  error_detail:  {e.error_detail}  (None means client-side cancel)")

        # Test concurrent invocations: fire multiple invoke_event calls at once
        # using asyncio.gather and verify all responses come back correctly.
        print("\n=== concurrent invokeEvent (3 x echo) ===")
        async def invoke_echo(index):
            result = await client.invoke_event(
                "echo", f"concurrent-{index}", WebPubSubDataType.TEXT
            )
            return index, result

        results = await asyncio.gather(
            invoke_echo(0), invoke_echo(1), invoke_echo(2),
            return_exceptions=True,
        )
        for item in results:
            if isinstance(item, Exception):
                print(f"  Concurrent invoke failed: {item}")
            else:
                index, result = item
                print(f"  [{index}] invocation_id={result.invocation_id}  "
                      f"data_type={result.data_type}  data={result.data}")


if __name__ == "__main__":
    asyncio.run(main())
