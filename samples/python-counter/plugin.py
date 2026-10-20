"""Beispiel-Plugin für Brim in Python, ohne Bibliotheken: ein Zähler im Tab „Plugins“.

Brim startet das Skript und gibt den Namen der Pipe in BRIM_PIPE mit. Das Protokoll ist
JSON-RPC 2.0, eine Nachricht pro Zeile (siehe docs/plugins.md).
"""
import itertools
import json
import os
import sys

pipe = open("\\\\.\\pipe\\" + os.environ["BRIM_PIPE"], "r+b", buffering=0)
ids = itertools.count(1)
count = 0


def send(message):
    pipe.write((json.dumps(message) + "\n").encode("utf-8"))


def request(method, params):
    request_id = next(ids)
    send({"jsonrpc": "2.0", "id": request_id, "method": method, "params": params})
    return request_id


def read():
    line = pipe.readline()
    if not line:
        sys.exit(0)  # Brim hat die Verbindung geschlossen
    return json.loads(line)


def show():
    request("view.set", {
        "id": "counter",
        "content": {
            "type": "stack", "spacing": 12, "padding": [12, 10, 12, 10],
            "background": "#1C1D21", "cornerRadius": 14,
            "children": [
                {"type": "text", "text": "Zähler", "style": "secondary", "grow": 1},
                {"type": "button", "actionId": "minus", "glyph": "", "size": 32, "glyphSize": 14, "accessibleName": "Weniger"},
                {"type": "text", "text": str(count), "style": "mono", "fontSize": 20, "fixedWidth": 48},
                {"type": "button", "actionId": "plus", "glyph": "", "size": 32, "glyphSize": 14, "accessibleName": "Mehr"},
                {"type": "button", "actionId": "reset", "kind": "secondary", "label": "Zurücksetzen"},
            ],
        },
    })


# Erst „hello“ und auf die Antwort warten; vorher nimmt Brim nichts anderes an.
hello_id = request("hello", {"api": "1"})
while True:
    message = read()
    if message.get("id") == hello_id:
        break
show()

while True:
    message = read()
    if "error" in message:
        print("Brim meldet:", message["error"].get("message"), file=sys.stderr, flush=True)
    method = message.get("method")
    if method == "shutdown":
        break
    if method == "action.invoked":
        action = message["params"]["actionId"]
        count = {"plus": count + 1, "minus": count - 1, "reset": 0}.get(action, count)
        show()
        request("hud.show", {"glyph": "" if action == "plus" else "", "text": f"Zähler: {count}"})
