from __future__ import annotations

import socketio


class SocketAI:
    """WebSocket client wrapper for blocking request/response calls."""

    def __init__(self, base_url: str = "http://127.0.0.1:5000", username: str = "", pet_id: str = ""):
        self.base_url = base_url
        self.username = username
        self.model_name="gpt"
        self.pet_id = pet_id
        print(self.pet_id)
        self.sio = socketio.Client()
        self._connect()

    def _connect(self):
        if self.sio.connected:
            return
        self.sio.connect(self.base_url, wait_timeout=10)

    def _ensure_connected(self):
        if not self.sio.connected or "/" not in self.sio.namespaces:
            try:
                self.sio.disconnect()
            except Exception:
                pass
            self._connect()

    def emit(self, event: str, data: dict, callback=None):
        """Send a fire-and-forget event with the username injected."""
        if self.sio.connected:
            data["username"] = self.username
            self.sio.emit(event, data, callback=callback)

    def wait_for_response(self, target: str, data, timeout: int = 100,special=None):
        payload = {
            "data": data,
            "username": self.username,
            "model_name": self.model_name,
            "pet_id": self.pet_id,
            "special":special
        }
        self._ensure_connected()
        try:
            return self.sio.call(target, payload, timeout=timeout)
        except (socketio.exceptions.BadNamespaceError, socketio.exceptions.DisconnectedError):
            self._ensure_connected()
            return self.sio.call(target, payload, timeout=timeout)

    def save_dialogue_history(self,history):
        return self.wait_for_response("save_dialogue_history",history)
    def update_summary_history(self,history,chat_begin_time):
        print("sendoi")
        return self.wait_for_response("update_summary_history",history,special=chat_begin_time)

    def get_dialogue_history(self):
        return self.wait_for_response("get_dialogue_history","")
    def get_summary_history(self):
        return self.wait_for_response("get_summary_history","")
    def translate(self, audio_data):
        return self.wait_for_response("audio_translate", audio_data)

    def get_answer(self, chat_history):
        return self.wait_for_response("get_answer", chat_history)

    def change_model(self,is_serious):
        return self.wait_for_response("change_model", is_serious)

    def voice_produce(self, dt):
        payload = dict(dt)
        if self.pet_id:
            payload["pet_id"] = self.pet_id
        return self.wait_for_response("voice_produce", payload)
