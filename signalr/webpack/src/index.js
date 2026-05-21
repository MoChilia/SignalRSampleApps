var __awaiter = (this && this.__awaiter) || function (thisArg, _arguments, P, generator) {
    function adopt(value) { return value instanceof P ? value : new P(function (resolve) { resolve(value); }); }
    return new (P || (P = Promise))(function (resolve, reject) {
        function fulfilled(value) { try { step(generator.next(value)); } catch (e) { reject(e); } }
        function rejected(value) { try { step(generator["throw"](value)); } catch (e) { reject(e); } }
        function step(result) { result.done ? resolve(result.value) : adopt(result.value).then(fulfilled, rejected); }
        step((generator = generator.apply(thisArg, _arguments || [])).next());
    });
};
import * as signalR from "@microsoft/signalr";
import "./css/main.css";
// Each browser window/tab runs its own JavaScript context
const divMessages = document.querySelector("#divMessages");
const tbMessage = document.querySelector("#tbMessage");
const btnSend = document.querySelector("#btnSend");
const tbGroupName = document.querySelector("#tbGroupName");
const btnJoinGroup = document.querySelector("#btnJoinGroup");
const btnLeaveGroup = document.querySelector("#btnLeaveGroup");
const username = new Date().getTime();
let currentGroup = null;
let isConnected = false;
// The HubConnectionBuilder class creates a new builder for configuring the server connection. 
// The withUrl function configures the hub URL.
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/hub")
    .build();
// SignalR enables the exchange of messages between a client and a server. Each message has a specific name. For example, messages with the name messageReceived can run the logic responsible for displaying the new message in the messages zone.
connection.on("messageReceived", (username, message) => {
    const m = document.createElement("div");
    m.innerHTML = `<div class="message-author">${username}</div><div>${message}</div>`;
    divMessages.appendChild(m);
    divMessages.scrollTop = divMessages.scrollHeight;
});
// Listen for group messages
connection.on("Send", (message) => {
    const m = document.createElement("div");
    m.innerHTML = `<div class="system-message">${message}</div>`;
    divMessages.appendChild(m);
    divMessages.scrollTop = divMessages.scrollHeight;
});
// Disable buttons initially until connected
btnSend.disabled = true;
btnJoinGroup.disabled = true;
btnLeaveGroup.disabled = true;
connection.start()
    .then(() => {
    console.log("Connected!");
    isConnected = true;
    updateGroupButtons();
})
    .catch((err) => {
    console.error("Connection failed:", err);
    document.write(err);
});
// Send message to group (or broadcast if not in a group)
function send() {
    if (!isConnected) {
        alert("Not connected to server");
        return;
    }
    const message = tbMessage.value.trim();
    if (!message) {
        return;
    }
    if (currentGroup) {
        // Send to group only
        connection.send("SendMessageToGroup", currentGroup, username, message)
            .then(() => (tbMessage.value = ""));
    }
    else {
        // Broadcast to all (no group)
        connection.send("NewMessage", username, message)
            .then(() => (tbMessage.value = ""));
    }
}
// Join group
function joinGroup() {
    return __awaiter(this, void 0, void 0, function* () {
        if (!isConnected) {
            alert("Not connected to server");
            return;
        }
        const groupName = tbGroupName.value.trim();
        if (!groupName) {
            alert("Please enter a group name");
            return;
        }
        try {
            yield connection.invoke("AddToGroup", groupName, username);
            currentGroup = groupName;
            updateGroupButtons();
            console.log(`Joined group: ${groupName}`);
        }
        catch (err) {
            console.error(`Failed to join group: ${err}`);
            alert(`Failed to join group: ${err}`);
        }
    });
}
// Leave group
function leaveGroup() {
    return __awaiter(this, void 0, void 0, function* () {
        if (!isConnected || !currentGroup) {
            return;
        }
        try {
            yield connection.invoke("RemoveFromGroup", currentGroup, username);
            console.log(`Left group: ${currentGroup}`);
            currentGroup = null;
            updateGroupButtons();
        }
        catch (err) {
            console.error(`Failed to leave group: ${err}`);
            alert(`Failed to leave group: ${err}`);
        }
    });
}
// Update button states based on group membership
function updateGroupButtons() {
    if (!isConnected) {
        btnJoinGroup.disabled = true;
        btnLeaveGroup.disabled = true;
        btnSend.disabled = true;
        tbGroupName.disabled = true;
        return;
    }
    if (currentGroup) {
        btnJoinGroup.disabled = true;
        btnLeaveGroup.disabled = false;
        btnSend.disabled = false;
        tbGroupName.disabled = true;
        tbGroupName.value = currentGroup;
    }
    else {
        btnJoinGroup.disabled = false;
        btnLeaveGroup.disabled = true;
        btnSend.disabled = false;
        tbGroupName.disabled = false;
        tbGroupName.value = "";
    }
}
tbMessage.addEventListener("keyup", (e) => {
    if (e.key === "Enter") {
        send();
    }
});
btnSend.addEventListener("click", send);
btnJoinGroup.addEventListener("click", joinGroup);
btnLeaveGroup.addEventListener("click", leaveGroup);
