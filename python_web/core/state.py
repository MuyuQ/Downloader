from __future__ import annotations

from dataclasses import dataclass, field
import threading
import time


@dataclass
class DownloadState:
    state: str = "idle"
    message: str = "Ready"
    progress: float = 0.0
    bytes_received: int = 0
    total_bytes: int = 0
    speed_bps: float = 0.0
    file_path: str = ""
    download_name: str = ""
    url: str = ""
    error: str = ""
    updated_at: float = field(default_factory=time.time)
    _lock: threading.Lock = field(default_factory=threading.Lock, init=False, repr=False)

    def update(self, **kwargs) -> None:
        with self._lock:
            for key, value in kwargs.items():
                setattr(self, key, value)
            self.updated_at = time.time()

    def snapshot(self) -> dict:
        with self._lock:
            return {
                "state": self.state,
                "message": self.message,
                "progress": round(self.progress, 2),
                "bytes_received": self.bytes_received,
                "total_bytes": self.total_bytes,
                "speed_bps": round(self.speed_bps, 2),
                "file_path": self.file_path,
                "download_name": self.download_name,
                "url": self.url,
                "error": self.error,
                "updated_at": self.updated_at,
            }
