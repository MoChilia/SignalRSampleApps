"use strict";
var __awaiter = (this && this.__awaiter) || function (thisArg, _arguments, P, generator) {
    function adopt(value) { return value instanceof P ? value : new P(function (resolve) { resolve(value); }); }
    return new (P || (P = Promise))(function (resolve, reject) {
        function fulfilled(value) { try { step(generator.next(value)); } catch (e) { reject(e); } }
        function rejected(value) { try { step(generator["throw"](value)); } catch (e) { reject(e); } }
        function step(result) { result.done ? resolve(result.value) : adopt(result.value).then(fulfilled, rejected); }
        step((generator = generator.apply(thisArg, _arguments || [])).next());
    });
};
var __generator = (this && this.__generator) || function (thisArg, body) {
    var _ = { label: 0, sent: function() { if (t[0] & 1) throw t[1]; return t[1]; }, trys: [], ops: [] }, f, y, t, g = Object.create((typeof Iterator === "function" ? Iterator : Object).prototype);
    return g.next = verb(0), g["throw"] = verb(1), g["return"] = verb(2), typeof Symbol === "function" && (g[Symbol.iterator] = function() { return this; }), g;
    function verb(n) { return function (v) { return step([n, v]); }; }
    function step(op) {
        if (f) throw new TypeError("Generator is already executing.");
        while (g && (g = 0, op[0] && (_ = 0)), _) try {
            if (f = 1, y && (t = op[0] & 2 ? y["return"] : op[0] ? y["throw"] || ((t = y["return"]) && t.call(y), 0) : y.next) && !(t = t.call(y, op[1])).done) return t;
            if (y = 0, t) op = [op[0] & 2, t.value];
            switch (op[0]) {
                case 0: case 1: t = op; break;
                case 4: _.label++; return { value: op[1], done: false };
                case 5: _.label++; y = op[1]; op = [0]; continue;
                case 7: op = _.ops.pop(); _.trys.pop(); continue;
                default:
                    if (!(t = _.trys, t = t.length > 0 && t[t.length - 1]) && (op[0] === 6 || op[0] === 2)) { _ = 0; continue; }
                    if (op[0] === 3 && (!t || (op[1] > t[0] && op[1] < t[3]))) { _.label = op[1]; break; }
                    if (op[0] === 6 && _.label < t[1]) { _.label = t[1]; t = op; break; }
                    if (t && _.label < t[2]) { _.label = t[2]; _.ops.push(op); break; }
                    if (t[2]) _.ops.pop();
                    _.trys.pop(); continue;
            }
            op = body.call(thisArg, _);
        } catch (e) { op = [6, e]; y = 0; } finally { f = t = 0; }
        if (op[0] & 5) throw op[1]; return { value: op[0] ? op[1] : void 0, done: true };
    }
};
Object.defineProperty(exports, "__esModule", { value: true });
var signalR = require("@microsoft/signalr");
require("./css/main.css");
// Each browser window/tab runs its own JavaScript context
var divMessages = document.querySelector("#divMessages");
var tbMessage = document.querySelector("#tbMessage");
var btnSend = document.querySelector("#btnSend");
var tbGroupName = document.querySelector("#tbGroupName");
var btnJoinGroup = document.querySelector("#btnJoinGroup");
var btnLeaveGroup = document.querySelector("#btnLeaveGroup");
var username = new Date().getTime();
var currentGroup = null;
var isConnected = false;
// The HubConnectionBuilder class creates a new builder for configuring the server connection. 
// The withUrl function configures the hub URL.
var connection = new signalR.HubConnectionBuilder()
    .withUrl("/hub")
    .build();
// SignalR enables the exchange of messages between a client and a server. Each message has a specific name. For example, messages with the name messageReceived can run the logic responsible for displaying the new message in the messages zone.
connection.on("messageReceived", function (username, message) {
    var m = document.createElement("div");
    m.innerHTML = "<div class=\"message-author\">".concat(username, "</div><div>").concat(message, "</div>");
    divMessages.appendChild(m);
    divMessages.scrollTop = divMessages.scrollHeight;
});
// Listen for group messages
connection.on("Send", function (message) {
    var m = document.createElement("div");
    m.innerHTML = "<div class=\"system-message\">".concat(message, "</div>");
    divMessages.appendChild(m);
    divMessages.scrollTop = divMessages.scrollHeight;
});
// Disable buttons initially until connected
btnSend.disabled = true;
btnJoinGroup.disabled = true;
btnLeaveGroup.disabled = true;
connection.start()
    .then(function () {
    console.log("Connected!");
    isConnected = true;
    updateGroupButtons();
})
    .catch(function (err) {
    console.error("Connection failed:", err);
    document.write(err);
});
// Send message to group (or broadcast if not in a group)
function send() {
    if (!isConnected) {
        alert("Not connected to server");
        return;
    }
    var message = tbMessage.value.trim();
    if (!message) {
        return;
    }
    if (currentGroup) {
        // Send to group only
        connection.send("SendMessageToGroup", currentGroup, username, message)
            .then(function () { return (tbMessage.value = ""); });
    }
    else {
        // Broadcast to all (no group)
        connection.send("NewMessage", username, message)
            .then(function () { return (tbMessage.value = ""); });
    }
}
// Join group
function joinGroup() {
    return __awaiter(this, void 0, void 0, function () {
        var groupName, err_1;
        return __generator(this, function (_a) {
            switch (_a.label) {
                case 0:
                    if (!isConnected) {
                        alert("Not connected to server");
                        return [2 /*return*/];
                    }
                    groupName = tbGroupName.value.trim();
                    if (!groupName) {
                        alert("Please enter a group name");
                        return [2 /*return*/];
                    }
                    _a.label = 1;
                case 1:
                    _a.trys.push([1, 3, , 4]);
                    return [4 /*yield*/, connection.invoke("AddToGroup", groupName)];
                case 2:
                    _a.sent();
                    currentGroup = groupName;
                    updateGroupButtons();
                    console.log("Joined group: ".concat(groupName));
                    return [3 /*break*/, 4];
                case 3:
                    err_1 = _a.sent();
                    console.error("Failed to join group: ".concat(err_1));
                    alert("Failed to join group: ".concat(err_1));
                    return [3 /*break*/, 4];
                case 4: return [2 /*return*/];
            }
        });
    });
}
// Leave group
function leaveGroup() {
    return __awaiter(this, void 0, void 0, function () {
        var err_2;
        return __generator(this, function (_a) {
            switch (_a.label) {
                case 0:
                    if (!isConnected || !currentGroup) {
                        return [2 /*return*/];
                    }
                    _a.label = 1;
                case 1:
                    _a.trys.push([1, 3, , 4]);
                    return [4 /*yield*/, connection.invoke("RemoveFromGroup", currentGroup)];
                case 2:
                    _a.sent();
                    console.log("Left group: ".concat(currentGroup));
                    currentGroup = null;
                    updateGroupButtons();
                    return [3 /*break*/, 4];
                case 3:
                    err_2 = _a.sent();
                    console.error("Failed to leave group: ".concat(err_2));
                    alert("Failed to leave group: ".concat(err_2));
                    return [3 /*break*/, 4];
                case 4: return [2 /*return*/];
            }
        });
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
tbMessage.addEventListener("keyup", function (e) {
    if (e.key === "Enter") {
        send();
    }
});
btnSend.addEventListener("click", send);
btnJoinGroup.addEventListener("click", joinGroup);
btnLeaveGroup.addEventListener("click", leaveGroup);
