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

def extract_metadata(headers):
    """Extract metadata from request headers with X-WebPubSub-Metadata- prefix (case-insensitive)"""
    metadata = {}
    prefix = 'x-webpubsub-metadata-'
    for key, value in headers.items():
        if key.lower().startswith(prefix):
            metadata_key = key[len(prefix):].lower()
            metadata[metadata_key] = value
    return metadata

@app.route('/eventhandler', methods=['POST', 'OPTIONS'])
def handle_event():
    # Debug: Print raw HTTP request details
    print("\n" + "="*60)
    print("RAW HTTP REQUEST DEBUG")
    print("="*60)
    print(f"Method: {request.method}")
    print(f"URL: {request.url}")
    print(f"Content-Type: {request.content_type}")
    print(f"Content-Length: {request.content_length}")
    print("-"*60)
    print("HEADERS:")
    for key, value in request.headers:
        print(f"  {key}: {value}")
    print("-"*60)
    print("BODY (raw):")
    print(request.data.decode('utf-8', errors='replace')[:1000])  # Limit to 1000 chars
    print("="*60 + "\n")
    
    if request.method == 'OPTIONS' or request.method == 'GET':
        if request.headers.get('WebHook-Request-Origin'):
            res = Response()
            res.headers['WebHook-Allowed-Origin'] = '*'
            res.status_code = 200
            return res
    elif request.method == 'POST':
        user_id = request.headers.get('ce-userid')
        type = request.headers.get('ce-type')
        event_name = request.headers.get('ce-eventname')
        print("Received event of type:", type)
        
        # Extract metadata from headers
        metadata = extract_metadata(request.headers)
        if metadata:
            print("Received metadata:", metadata)
        
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
        elif type == 'azure.webpubsub.sys.disconnected':
            reason = request.headers.get('ce-reason', 'unknown')
            print(f"User {user_id} disconnected. Reason: {reason}")
            return Response(status=204)
        elif type == 'azure.webpubsub.user.message':
            service.send_to_all(content_type="application/json", message={
                'from': user_id,
                'message': request.data.decode('UTF-8')
            })
            return Response(status=204, content_type='text/plain')
        elif type.startswith('azure.webpubsub.user.') and type != 'azure.webpubsub.user.message':
            # Handle custom user events sent via sendEvent
            data = request.data.decode('UTF-8')
            print(f"Received custom event '{event_name}' from user {user_id}")
            print(f"Event data: {data}")
            print(f"Received metadata from client: {metadata}")
            
            # Validate metadata was received
            metadata_received = len(metadata) > 0
            received_keys = list(metadata.keys())
            print(f"Metadata validation - received: {metadata_received}, keys: {received_keys}")
            
            # Echo event with metadata back to all clients
            if event_name == 'echo':
                # Send response with metadata headers (echoing back what we received)
                response_data = {
                    'echo': data,
                    'from': user_id,
                    'validation': {
                        'metadataReceivedByServer': metadata_received,
                        'receivedMetadataKeys': received_keys,
                        'receivedMetadata': metadata
                    }
                }
                res = Response(
                    response=json.dumps(response_data),
                    status=200,
                    content_type='application/json'
                )
                # Add metadata to response headers to test server->client metadata
                res.headers['X-WebPubSub-Metadata-EventType'] = 'echo-response'
                res.headers['X-WebPubSub-Metadata-ProcessedBy'] = 'server'
                res.headers['X-WebPubSub-Metadata-ServerValidation'] = 'metadata-received' if metadata_received else 'no-metadata'
                # Echo back the traceid if received
                if 'traceid' in metadata:
                    res.headers['X-WebPubSub-Metadata-EchoedTraceId'] = metadata['traceid']
                if 'topic' in metadata:
                    res.headers['X-WebPubSub-Metadata-EchoedTopic'] = metadata['topic']
                if 'priority' in metadata:
                    res.headers['X-WebPubSub-Metadata-EchoedPriority'] = metadata['priority']
                return res
            else:
                # Default: acknowledge with metadata validation
                response_data = {
                    'received': event_name,
                    'validation': {
                        'metadataReceivedByServer': metadata_received,
                        'receivedMetadataKeys': received_keys,
                        'receivedMetadata': metadata
                    }
                }
                res = Response(
                    response=json.dumps(response_data),
                    status=200,
                    content_type='application/json'
                )
                res.headers['X-WebPubSub-Metadata-Status'] = 'acknowledged'
                res.headers['X-WebPubSub-Metadata-ServerValidation'] = 'metadata-received' if metadata_received else 'no-metadata'
                return res
        else:
            return 'Bad Request', 400


if __name__ == '__main__':
    app.run(port=8080)