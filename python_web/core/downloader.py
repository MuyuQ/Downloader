from __future__ import annotations

from pathlib import Path
from urllib.parse import urlparse, unquote
from urllib.request import Request, urlopen
import os
import threading
import time

from .config import ConfigStore, ConfigError
from .extractor import ExtractError, ExtractionCanceled, safe_extract_zip
from .state import DownloadState


class DownloadCanceled(Exception):
    pass


_INVALID_FILENAME_CHARS = '<>:"/\\|?*'


def _sanitize_filename(name: str) -> str:
    cleaned = name.translate({ord(ch): "_" for ch in _INVALID_FILENAME_CHARS})
    cleaned = cleaned.strip().rstrip(".")
    return cleaned or "download"


def _unique_path(directory: Path, filename: str) -> Path:
    candidate = directory / filename
    if not candidate.exists():
        return candidate

    base = Path(filename)
    stem = base.stem or "download"
    suffix = base.suffix
    counter = 1
    while True:
        candidate = directory / f"{stem}_{counter}{suffix}"
        if not candidate.exists():
            return candidate
        counter += 1


class DownloadService:
    def __init__(self, config_store: ConfigStore, state: DownloadState | None = None):
        self.config_store = config_store
        self.state = state or DownloadState()
        self._thread: threading.Thread | None = None
        self._cancel_event = threading.Event()
        self._lock = threading.Lock()

    def is_busy(self) -> bool:
        return self._thread is not None and self._thread.is_alive()

    def start(self, download_name: str, url: str, auto_extract_override: bool | None = None) -> None:
        with self._lock:
            if self.is_busy():
                raise RuntimeError("Download already in progress")

            self._cancel_event = threading.Event()
            thread = threading.Thread(
                target=self._run,
                args=(download_name, url, auto_extract_override),
                daemon=True,
            )
            self._thread = thread
            thread.start()

    def cancel(self) -> None:
        if self.is_busy():
            self._cancel_event.set()
            self.state.update(message="Canceling...")

    def _run(self, download_name: str, url: str, auto_extract_override: bool | None) -> None:
        temp_path = None
        final_path = None
        try:
            config = self.config_store.load()

            save_dir = config.save_directory
            save_dir.mkdir(parents=True, exist_ok=True)

            parsed = urlparse(url)
            raw_name = Path(parsed.path).name
            filename = _sanitize_filename(unquote(raw_name))
            if not filename:
                filename = f"download_{time.strftime('%Y%m%d_%H%M%S')}"

            final_path = _unique_path(save_dir, filename)
            temp_path = final_path.with_suffix(final_path.suffix + ".part")

            self.state.update(
                state="downloading",
                message="Connecting...",
                progress=0.0,
                bytes_received=0,
                total_bytes=0,
                speed_bps=0.0,
                file_path=str(final_path),
                download_name=download_name,
                url=url,
                error="",
            )

            request = Request(url, headers={"User-Agent": config.user_agent})
            with urlopen(request, timeout=30) as response:
                total = response.headers.get("Content-Length")
                total_bytes = int(total) if total and total.isdigit() else 0

                self.state.update(message="Downloading...", total_bytes=total_bytes)

                bytes_received = 0
                last_time = time.monotonic()
                last_bytes = 0

                with open(temp_path, "wb") as output:
                    while True:
                        if self._cancel_event.is_set():
                            raise DownloadCanceled("Canceled by user")

                        chunk = response.read(1024 * 64)
                        if not chunk:
                            break

                        output.write(chunk)
                        bytes_received += len(chunk)

                        now = time.monotonic()
                        elapsed = now - last_time
                        if elapsed >= 0.5:
                            speed = (bytes_received - last_bytes) / max(elapsed, 0.001)
                            last_time = now
                            last_bytes = bytes_received
                        else:
                            speed = self.state.snapshot()["speed_bps"]

                        progress = (bytes_received / total_bytes * 100) if total_bytes else 0.0
                        self.state.update(
                            bytes_received=bytes_received,
                            progress=progress,
                            speed_bps=speed,
                        )

            if self._cancel_event.is_set():
                raise DownloadCanceled("Canceled by user")

            if temp_path.exists():
                temp_path.replace(final_path)

            auto_extract = auto_extract_override if auto_extract_override is not None else config.auto_extract_zip

            if auto_extract and final_path.suffix.lower() == ".zip":
                self._extract_zip(final_path, save_dir)

            self.state.update(state="completed", message="Download complete", progress=100.0, speed_bps=0.0)

        except DownloadCanceled:
            self._cleanup_temp(temp_path)
            self.state.update(state="canceled", message="Download canceled", speed_bps=0.0)
        except (ExtractError, ExtractionCanceled) as exc:
            self.state.update(state="error", message="Extraction failed", error=str(exc), speed_bps=0.0)
        except ConfigError as exc:
            self.state.update(state="error", message="Config error", error=str(exc), speed_bps=0.0)
        except Exception as exc:
            self._cleanup_temp(temp_path)
            self.state.update(state="error", message="Download failed", error=str(exc), speed_bps=0.0)
        finally:
            if self.state.state in {"error", "canceled"}:
                self._cleanup_temp(temp_path)

    def _extract_zip(self, zip_path: Path, target_root: Path) -> None:
        self.state.update(state="extracting", message="Extracting...", progress=0.0, speed_bps=0.0)

        def _progress(progress):
            percent = progress.current / max(progress.total, 1) * 100.0
            self.state.update(
                progress=percent,
                bytes_received=progress.current,
                total_bytes=progress.total,
                message=f"Extracting... ({progress.current}/{progress.total})",
            )

        safe_extract_zip(zip_path, target_root, progress_cb=_progress, cancel_event=self._cancel_event)

    @staticmethod
    def _cleanup_temp(temp_path: Path | None) -> None:
        if temp_path and temp_path.exists():
            try:
                temp_path.unlink()
            except OSError:
                pass
