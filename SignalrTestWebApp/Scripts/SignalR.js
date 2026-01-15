(function () {
    $("#click-me").on("click", function () {
        myHub.server.getServerTime()
            .done(function (data) {
                writeToPage(data);
            })
            .fail(function (e) {
                writeToPage(e);
            });
    })

    var myHub = $.connection.myHub;
    $.connection.hub.start()
        .done(function () {
            $.connection.hub.logging = true;
            $.connection.hub.log("Connected.");
            console.log("Connect to SignalR hub.");
            myHub.server.announce("Client connected.");
        })
        .fail(function () { alert("Could not connect to SignalR hub."); });

    myHub.client.announce = function (message) {
        writeToPage(message);
    };

    var writeToPage = function (message) {
        $("#welcome-messages").append(message + "<br />");
    }
})()
