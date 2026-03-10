package com.azure.messaging.webpubsub.client;
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

import com.azure.core.util.BinaryData;
import com.azure.core.util.Configuration;
import com.azure.messaging.webpubsub.WebPubSubServiceClient;
import com.azure.messaging.webpubsub.WebPubSubServiceClientBuilder;
import com.azure.messaging.webpubsub.client.models.InvocationException;
import com.azure.messaging.webpubsub.client.models.InvokeEventOptions;
import com.azure.messaging.webpubsub.client.models.InvokeEventResult;
import com.azure.messaging.webpubsub.client.models.WebPubSubClientCredential;
import com.azure.messaging.webpubsub.client.models.WebPubSubDataFormat;
import com.azure.messaging.webpubsub.models.GetClientAccessTokenOptions;

import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public class invoke_event {

    public static void main(String[] args) throws Exception {

        final String hubName = "chat";
        final String userName = "user1";

        // Prepare the clientCredential
        WebPubSubServiceClient serverClient = new WebPubSubServiceClientBuilder()
            .connectionString(Configuration.getGlobalConfiguration().get("WEBPUBSUB_CONNECTION_STRING"))
            .hub(hubName)
            .buildClient();

        WebPubSubClientCredential clientCredential = new WebPubSubClientCredential(
            () -> serverClient.getClientAccessToken(new GetClientAccessTokenOptions()
                    .setUserId(userName)
                    .addRole("webpubsub.joinLeaveGroup")
                    .addRole("webpubsub.sendToGroup"))
                .getUrl());

        // Create client
        WebPubSubClient client = new WebPubSubClientBuilder()
            .credential(clientCredential)
            .buildClient();

        // Event handlers
        client.addOnConnectedEventHandler(event -> {
            System.out.println("======== connected ===========");
            System.out.println("Connection " + event.getConnectionId() + " is connected");
        });
        client.addOnDisconnectedEventHandler(event -> {
            System.out.println("========== disconnected =========");
            System.out.println("Connection is disconnected: " + event.getReason());
        });

        // Start client
        client.start();

        // ── Test 1: Invoke event with JSON data (processOrder) ──────────
        try {
            InvokeEventResult result = client.invokeEvent("processOrder",
                BinaryData.fromString("{\"orderId\": 1}"), WebPubSubDataFormat.JSON,
                new InvokeEventOptions().setTimeout(Duration.ofSeconds(60)));
            System.out.println("=== invokeResponse (processOrder) ===");
            System.out.println("  invocation_id : " + result.getInvocationId());
            System.out.println("  data_type     : " + result.getDataFormat());
            System.out.println("  data          : " + result.getData().toString());
        } catch (InvocationException e) {
            System.out.println("Invocation failed: " + e.getMessage());
            if (e.getErrorDetail() != null) {
                System.out.println("  error name: " + e.getErrorDetail().getName()
                    + ", message: " + e.getErrorDetail().getMessage());
            }
        }

        // ── Test 2: Invoke event with text data (echo) ─────────────────
        try {
            InvokeEventResult result = client.invokeEvent("echo",
                BinaryData.fromString("hello"), WebPubSubDataFormat.TEXT);
            System.out.println("=== invokeResponse (echo) ===");
            System.out.println("  invocation_id : " + result.getInvocationId());
            System.out.println("  data_type     : " + result.getDataFormat());
            System.out.println("  data          : " + result.getData().toString());
        } catch (InvocationException e) {
            System.out.println("Echo failed: " + e.getMessage());
        }

        // ── Test 3: Server error propagation (processOrderError) ───────
        try {
            System.out.println("\n=== invokeEvent (processOrderError) — expecting server error ===");
            InvokeEventResult result = client.invokeEvent("processOrderError",
                BinaryData.fromString("{\"orderId\": 42}"), WebPubSubDataFormat.JSON,
                new InvokeEventOptions().setTimeout(Duration.ofSeconds(30)));
            // If we get here, the error was NOT propagated correctly
            System.out.println("  BUG: expected InvocationException but got success: "
                + result.getData().toString());
        } catch (InvocationException e) {
            System.out.println("  Correctly received InvocationException:");
            System.out.println("    invocation_id : " + e.getInvocationId());
            System.out.println("    message       : " + e.getMessage());
            if (e.getErrorDetail() != null) {
                System.out.println("    error name    : " + e.getErrorDetail().getName());
                System.out.println("    error message : " + e.getErrorDetail().getMessage());
            } else {
                System.out.println("    error_detail  : null");
            }
        }

        // ── Test 4: Cancel invocation via short timeout (slowEvent) ────
        try {
            System.out.println("\n=== invokeEvent (slowEvent) — expecting timeout & cancel ===");
            InvokeEventResult result = client.invokeEvent("slowEvent",
                BinaryData.fromString("{\"delay\": 30}"), WebPubSubDataFormat.JSON,
                new InvokeEventOptions().setTimeout(Duration.ofSeconds(3)));  // short timeout triggers cancellation
            // Should not reach here
            System.out.println("  Unexpected success: " + result.getData().toString());
        } catch (InvocationException e) {
            System.out.println("  Caught expected InvocationException: " + e.getMessage());
            System.out.println("  invocation_id: " + e.getInvocationId());
            System.out.println("  error_detail:  " + e.getErrorDetail()
                + "  (null means client-side cancel)");
        }

        // ── Test 5: Concurrent invocations (3 x echo) ─────────────────
        System.out.println("\n=== concurrent invokeEvent (3 x echo) ===");
        ExecutorService executor = Executors.newFixedThreadPool(3);
        List<CompletableFuture<Void>> futures = new ArrayList<>();

        for (int i = 0; i < 3; i++) {
            final int index = i;
            CompletableFuture<Void> future = CompletableFuture.runAsync(() -> {
                try {
                    InvokeEventResult result = client.invokeEvent("echo",
                        BinaryData.fromString("concurrent-" + index), WebPubSubDataFormat.TEXT,
                        new InvokeEventOptions().setTimeout(Duration.ofSeconds(30)));
                    System.out.println("  [" + index + "] invocation_id=" + result.getInvocationId()
                        + "  data_type=" + result.getDataFormat()
                        + "  data=" + result.getData().toString());
                } catch (InvocationException e) {
                    System.out.println("  Concurrent invoke failed: " + e.getMessage());
                }
            }, executor);
            futures.add(future);
        }

        // Wait for all concurrent invocations to complete
        CompletableFuture.allOf(futures.toArray(new CompletableFuture<?>[0])).join();
        executor.shutdown();

        // Stop client
        client.stop();
    }
}
