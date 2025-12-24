from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
import configparser
import os


class ConfigError(Exception):
    pass


@dataclass(frozen=True)
class ConfigData:
    downloads: list[tuple[str, str]]
    announcement_title: str
    announcement_lines: list[str]
    auto_extract_zip: bool
    save_directory: Path
    user_agent: str


class ConfigStore:
    def __init__(self, path: Path):
        self.path = Path(path)

    def load(self) -> ConfigData:
        if not self.path.exists():
            raise ConfigError(f"Config file not found: {self.path}")

        parser = configparser.ConfigParser()
        parser.optionxform = str
        parser.read(self.path, encoding="utf-8")

        downloads: list[tuple[str, str]] = []
        if parser.has_section("Downloads"):
            for key, value in parser.items("Downloads"):
                name = key.strip()
                url = value.strip()
                if name and url:
                    downloads.append((name, url))

        if not downloads:
            raise ConfigError("No downloads configured in [Downloads].")

        title = parser.get("Announcement", "Title", fallback="WY Downloader")
        content_raw = parser.get("Announcement", "Content", fallback="")
        lines = [line.strip() for line in content_raw.split("|") if line.strip()]

        auto_extract = parser.getboolean("Settings", "AutoExtractZip", fallback=False)
        save_dir_raw = parser.get("Settings", "SaveDirectory", fallback="")
        save_dir = Path(save_dir_raw).expanduser() if save_dir_raw else Path(os.getcwd())
        user_agent = parser.get("Settings", "UserAgent", fallback="WYDownloader/1.0")

        return ConfigData(
            downloads=downloads,
            announcement_title=title,
            announcement_lines=lines,
            auto_extract_zip=auto_extract,
            save_directory=save_dir,
            user_agent=user_agent,
        )

    def get_download_url(self, name: str) -> str:
        data = self.load()
        for item_name, url in data.downloads:
            if item_name == name:
                return url
        raise ConfigError(f"Download not found: {name}")
