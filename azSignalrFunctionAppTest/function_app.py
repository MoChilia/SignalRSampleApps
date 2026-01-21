import azure.functions as func
import os
import requests
import json 

app = func.FunctionApp()

etag = ''
start_count = 0

@app.route(route="index", auth_level=func.AuthLevel.ANONYMOUS)
def index(req: func.HttpRequest) -> func.HttpResponse:
    f = open(os.path.dirname(os.path.realpath(__file__)) + '/content/index.html')
    return func.HttpResponse(f.read(), mimetype='text/html')

@app.route(route="negotiate", auth_level=func.AuthLevel.ANONYMOUS, methods=["POST"])
@app.generic_input_binding(arg_name="connectionInfo", type="signalRConnectionInfo", hubName="serverless", connectionStringSetting="AzureSignalRConnectionString")
def negotiate(req: func.HttpRequest, connectionInfo) -> func.HttpResponse:
    return func.HttpResponse(connectionInfo)

@app.timer_trigger(schedule="*/1 * * * *", arg_name="myTimer",
              run_on_startup=False,
              use_monitor=False)
@app.generic_output_binding(arg_name="signalRMessages", type="signalR", hubName="serverless", connectionStringSetting="AzureSignalRConnectionString")
def broadcast(myTimer: func.TimerRequest, signalRMessages: func.Out[str]) -> None:
    global etag
    global start_count
    headers = {'User-Agent': 'serverless', 'If-None-Match': etag}
    res = requests.get('https://api.github.com/repos/azure/azure-functions-python-worker', headers=headers)
    if res.headers.get('ETag'):
        etag = res.headers.get('ETag')

    if res.status_code == 200:
        jres = res.json()
        start_count = jres['stargazers_count']

    signalRMessages.set(json.dumps({
        'target': 'newMessage',
        'arguments': [ 'Current star count of https://api.github.com/repos/azure/azure-functions-python-worker is: ' + str(start_count) ]
    }))