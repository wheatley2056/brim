"""Beispiel-Plugin für Brim in Python, ohne Bibliotheken: ein Zähler als Widget auf „Jetzt“.

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
sizes = []  # Größen, in denen das Widget gerade liegt (aus „widget.placed“)


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
    # Inhalt nur für die gelegten Größen; liegt das Widget nirgends, gibt es nichts zu tun.
    for size in sizes:
        request("widget.set", {
            "id": "counter",
            "size": size,
            "content": {
                "type": "stack", "orientation": "vertical", "spacing": 6, "align": "stretch", "grow": 1,
                "children": [
                    {"type": "text", "text": "Zähler", "style": "caption"},
                    {"type": "text", "text": str(count), "style": "mono", "fontSize": 24, "grow": 1},
                    {"type": "stack", "spacing": 8, "children": [
                        {"type": "button", "actionId": "minus", "glyph": "\ue738", "size": 28, "glyphSize": 12, "accessibleName": "Weniger"},
                        {"type": "button", "actionId": "plus", "glyph": "\ue710", "size": 28, "glyphSize": 12, "accessibleName": "Mehr"},
                        {"type": "button", "actionId": "reset", "kind": "secondary", "label": "0"},
                    ]},
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
    if method == "widget.placed":
        sizes = next((w["sizes"] for w in message["params"]["widgets"] if w["id"] == "counter"), [])
        show()
    if method == "action.invoked":
        action = message["params"]["actionId"]
        count = {"plus": count + 1, "minus": count - 1, "reset": 0}.get(action, count)
        show()
        request("hud.show", {"glyph": "" if action == "plus" else "", "text": f"Zähler: {count}"})
