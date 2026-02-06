import os
import json

from dotenv import load_dotenv
from flask import (
    Flask, 
    request, 
    send_from_directory,
    Response
)

from azure.messaging.webpubsubservice import (
    WebPubSubServiceClient
)

load_dotenv()

hub_name = 'ChatSampleHub'
connection_string = os.environ.get('WebPubSubConnectionString')

app = Flask(__name__)
service = WebPubSubServiceClient.from_connection_string(connection_string, hub=hub_name)

@app.route('/<path:filename>')
def index(filename):
    return send_from_directory('public', filename)


@app.route('/negotiate')
def negotiate():
    id = request.args.get('id')
    if not id:
        return 'missing user id', 400

    token = service.get_client_access_token(user_id=id)
    return {
        'url': token['url']
    }, 200

@app.route('/eventhandler', methods=['POST', 'OPTIONS'])
def handle_event():
    if request.method == 'OPTIONS' or request.method == 'GET':
        if request.headers.get('WebHook-Request-Origin'):
            res = Response()
            res.headers['WebHook-Allowed-Origin'] = '*'
            res.status_code = 200
            return res
    elif request.method == 'POST':
        user_id = request.headers.get('ce-userid')
        type = request.headers.get('ce-type')
        print("Received event of type:", type)
        # Sample connect logic if connect event handler is configured
        if type == 'azure.webpubsub.sys.connect':
            body = request.data.decode('utf-8')
            print("Reading from connect request body...")
            query = json.loads(body)['query']
            print("Reading from request body query:", query)
            id_element = query.get('id')
            user_id = id_element[0] if id_element else None
            if user_id:
                return {'userId': user_id}, 200
            return 'missing user id', 401
        elif type == 'azure.webpubsub.sys.connected':
            return user_id + ' connected', 200
        elif type == 'azure.webpubsub.user.message':
            service.send_to_all(content_type="application/json", message={
                'from': user_id,
                'message': request.data.decode('UTF-8')
            })
            return Response(status=204, content_type='text/plain')
        else:
            return 'Bad Request', 400


if __name__ == '__main__':
    app.run(port=8080)