from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import os
import shutil
import zipfile


class ExtractError(Exception):
    pass


class ExtractionCanceled(Exception):
    pass


@dataclass(frozen=True)
class ExtractProgress:
    current: int
    total: int
    name: str


def _unique_dir(base_dir: Path) -> Path:
    candidate = base_dir
    counter = 1
    while candidate.exists():
        candidate = base_dir.parent / f"{base_dir.name}_{counter}"
        counter += 1
    candidate.mkdir(parents=True, exist_ok=False)
    return candidate


def safe_extract_zip(
    zip_path: Path,
    target_root: Path,
    progress_cb=None,
    cancel_event=None,
) -> Path:
    if not zip_path.exists():
        raise ExtractError(f"Zip file not found: {zip_path}")

    target_root.mkdir(parents=True, exist_ok=True)
    dest_dir = _unique_dir(target_root / zip_path.stem)
    dest_dir_real = dest_dir.resolve()

    with zipfile.ZipFile(zip_path, "r") as zf:
        members = zf.infolist()
        total = max(len(members), 1)
        for index, member in enumerate(members, start=1):
            if cancel_event and cancel_event.is_set():
                raise ExtractionCanceled("Extraction canceled")

            member_name = member.filename
            dest_path = (dest_dir / member_name).resolve()
            if not str(dest_path).startswith(str(dest_dir_real) + os.sep):
                raise ExtractError(f"Unsafe path in zip: {member_name}")

            if member.is_dir():
                dest_path.mkdir(parents=True, exist_ok=True)
            else:
                dest_path.parent.mkdir(parents=True, exist_ok=True)
                with zf.open(member, "r") as src, open(dest_path, "wb") as dst:
                    shutil.copyfileobj(src, dst)

            if progress_cb:
                progress_cb(ExtractProgress(index, total, member_name))

    return dest_dir
