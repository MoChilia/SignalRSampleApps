(function () {
    $.connection.hub.start()
        .done(function () {
            console.log("Connect to SignalR hub.");
            $.connection.myHub.server.announce("Client connected.");
        })
        .fail(function () { alert("Could not connect to SignalR hub."); });
    $.connection.myHub.client.announce = function (message) {
        $("#welcome-messages").append(message + "<br />");
    }
})()
