import json
import socket
import threading
from raft_traffic_probe import Capture


def exchange(size, capture_port=True):
    listener = socket.socket()
    listener.bind(('127.0.0.1', 0))
    listener.listen()
    port = listener.getsockname()[1]
    def server():
        with listener, listener.accept()[0] as stream:
            received = bytearray()
            while len(received) < size:
                received.extend(stream.recv(min(65536, size-len(received))))
            stream.sendall(received)
    serving = threading.Thread(target=server)
    serving.start()
    capture = Capture([port] if capture_port else [1]).start()
    with socket.create_connection(('127.0.0.1', port)) as client:
        client.sendall(b'a'*size)
        received=0
        while received < size:
            received += len(client.recv(size-received))
    serving.join(5)
    report=capture.finish()
    expected=size*2 if capture_port else 0
    assert report['counts'].get('tcp_payload_bytes',0)==expected, report
    assert report['kernel_drops']==0, report
    return {'size_each_direction':size,'capture_port':capture_port,'expected_tcp_payload_bytes':expected,**report}


reports=[exchange(1048576),exchange(65536,False)]
print(json.dumps({'passed':True,'cases':reports},indent=2))
