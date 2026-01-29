const { app, trigger } = require('@azure/functions');

// Handle SignalR connected event
app.generic('connected', {
    trigger: trigger.generic({
        type: 'signalRTrigger',
        name: 'invocationContext',
        hubName: 'default',
        category: 'connections',
        event: 'connected',
        connectionStringSetting: 'AzureSignalRConnectionString',
    }),
    handler: async (triggerInput, context) => {
        context.log(`Client connected: ${triggerInput.connectionId}`);
        return;
    }
});

// Handle SignalR disconnected event
app.generic('disconnected', {
    trigger: trigger.generic({
        type: 'signalRTrigger',
        name: 'invocationContext',
        hubName: 'default',
        category: 'connections',
        event: 'disconnected',
        connectionStringSetting: 'AzureSignalRConnectionString',
    }),
    handler: async (triggerInput, context) => {
        context.log(`Client disconnected: ${triggerInput.connectionId}`);
        return;
    }
});
