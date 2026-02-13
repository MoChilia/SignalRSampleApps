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

    token = service.get_client_access_token(
        user_id=id,
        roles=[
            'webpubsub.joinLeaveGroup',
            'webpubsub.sendToGroup'
        ]
    )
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
                if 'traceid' in metadata:
                    res.headers['X-WebPubSub-Metadata-EchoedTraceId'] = metadata['traceid']
                if 'topic' in metadata:
                    res.headers['X-WebPubSub-Metadata-EchoedTopic'] = metadata['topic']
                if 'priority' in metadata:
                    res.headers['X-WebPubSub-Metadata-EchoedPriority'] = metadata['priority']
                return res
            elif event_name == 'processDocument':
                # Invoke test: success with body and metadata
                response_data = {
                    'pages': 12,
                    'wordCount': 4500,
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
                res.headers['X-WebPubSub-Metadata-Status'] = 'completed'
                res.headers['X-WebPubSub-Metadata-ProcessingTime'] = '142ms'
                res.headers['X-WebPubSub-Metadata-OutputFile'] = 'report-processed.pdf'
                if 'traceid' in metadata:
                    res.headers['X-WebPubSub-Metadata-EchoedTraceId'] = metadata['traceid']
                if 'filename' in metadata:
                    res.headers['X-WebPubSub-Metadata-EchoedFilename'] = metadata['filename']
                return res
            elif event_name == 'metadataOnly':
                # Invoke test: success with metadata but no body (204)
                res = Response(status=204)
                res.headers['X-WebPubSub-Metadata-Status'] = 'accepted'
                res.headers['X-WebPubSub-Metadata-JobId'] = f'job-{int(__import__("time").time())}'
                if 'traceid' in metadata:
                    res.headers['X-WebPubSub-Metadata-EchoedTraceId'] = metadata['traceid']
                return res
            elif event_name == 'errorTest':
                # Invoke test: error response with metadata
                res = Response(
                    response='Simulated error for testing',
                    status=500,
                    content_type='text/plain'
                )
                res.headers['X-WebPubSub-Metadata-TraceId'] = metadata.get('traceid', 'unknown')
                res.headers['X-WebPubSub-Metadata-Reason'] = 'simulated-error'
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


# =============================================================
# REST API metadata test endpoints
# =============================================================

@app.route('/test/send-to-all', methods=['POST'])
def test_send_to_all():
    """Test metadata support in SendToAll REST API"""
    body = request.get_json(force=True) if request.data else {}
    message = body.get('message', 'Hello from SendToAll!')
    metadata = body.get('metadata', {})

    headers = {f'X-WebPubSub-Metadata-{k}': v for k, v in metadata.items()}
    headers['X-WebPubSub-Metadata-Source'] = 'rest-send-to-all'
    headers['X-WebPubSub-Metadata-Timestamp'] = str(int(__import__('time').time()))

    try:
        service.send_to_all(
            message={'test': 'SendToAll', 'payload': message, 'sentMetadata': {**metadata, 'source': 'rest-send-to-all'}},
            content_type='application/json',
            headers=headers
        )
        return {'success': True, 'api': 'SendToAll', 'metadataSent': headers}, 200
    except Exception as e:
        return {'success': False, 'api': 'SendToAll', 'error': str(e)}, 500


@app.route('/test/send-to-connection', methods=['POST'])
def test_send_to_connection():
    """Test metadata support in SendToConnection REST API"""
    body = request.get_json(force=True) if request.data else {}
    connection_id = body.get('connectionId')
    if not connection_id:
        return {'success': False, 'error': 'connectionId is required'}, 400

    message = body.get('message', 'Hello from SendToConnection!')
    metadata = body.get('metadata', {})

    headers = {f'X-WebPubSub-Metadata-{k}': v for k, v in metadata.items()}
    headers['X-WebPubSub-Metadata-Source'] = 'rest-send-to-connection'
    headers['X-WebPubSub-Metadata-TargetConnection'] = connection_id

    try:
        service.send_to_connection(
            connection_id=connection_id,
            message={'test': 'SendToConnection', 'payload': message, 'sentMetadata': {**metadata, 'source': 'rest-send-to-connection'}},
            content_type='application/json',
            headers=headers
        )
        return {'success': True, 'api': 'SendToConnection', 'connectionId': connection_id, 'metadataSent': headers}, 200
    except Exception as e:
        return {'success': False, 'api': 'SendToConnection', 'error': str(e)}, 500


@app.route('/test/send-to-group', methods=['POST'])
def test_send_to_group():
    """Test metadata support in SendToGroup REST API"""
    body = request.get_json(force=True) if request.data else {}
    group = body.get('group', 'testGroup')
    message = body.get('message', 'Hello from SendToGroup!')
    metadata = body.get('metadata', {})

    headers = {f'X-WebPubSub-Metadata-{k}': v for k, v in metadata.items()}
    headers['X-WebPubSub-Metadata-Source'] = 'rest-send-to-group'
    headers['X-WebPubSub-Metadata-Group'] = group

    try:
        service.send_to_group(
            group=group,
            message={'test': 'SendToGroup', 'payload': message, 'group': group, 'sentMetadata': {**metadata, 'source': 'rest-send-to-group'}},
            content_type='application/json',
            headers=headers
        )
        return {'success': True, 'api': 'SendToGroup', 'group': group, 'metadataSent': headers}, 200
    except Exception as e:
        return {'success': False, 'api': 'SendToGroup', 'error': str(e)}, 500


@app.route('/test/send-to-user', methods=['POST'])
def test_send_to_user():
    """Test metadata support in SendToUser REST API"""
    body = request.get_json(force=True) if request.data else {}
    user_id = body.get('userId')
    if not user_id:
        return {'success': False, 'error': 'userId is required'}, 400

    message = body.get('message', 'Hello from SendToUser!')
    metadata = body.get('metadata', {})

    headers = {f'X-WebPubSub-Metadata-{k}': v for k, v in metadata.items()}
    headers['X-WebPubSub-Metadata-Source'] = 'rest-send-to-user'
    headers['X-WebPubSub-Metadata-TargetUser'] = user_id

    try:
        service.send_to_user(
            user_id=user_id,
            message={'test': 'SendToUser', 'payload': message, 'sentMetadata': {**metadata, 'source': 'rest-send-to-user'}},
            content_type='application/json',
            headers=headers
        )
        return {'success': True, 'api': 'SendToUser', 'userId': user_id, 'metadataSent': headers}, 200
    except Exception as e:
        return {'success': False, 'api': 'SendToUser', 'error': str(e)}, 500


@app.route('/test/add-to-group', methods=['POST'])
def test_add_to_group():
    """Helper: add a connection to a group for testing SendToGroup"""
    body = request.get_json(force=True) if request.data else {}
    connection_id = body.get('connectionId')
    group = body.get('group', 'testGroup')
    if not connection_id:
        return {'success': False, 'error': 'connectionId is required'}, 400
    try:
        service.add_connection_to_group(group=group, connection_id=connection_id)
        return {'success': True, 'group': group, 'connectionId': connection_id}, 200
    except Exception as e:
        return {'success': False, 'error': str(e)}, 500


if __name__ == '__main__':
    app.run(port=8080)