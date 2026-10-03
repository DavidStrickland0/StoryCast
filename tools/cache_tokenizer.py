"""Warm the tokenizer dependency in the persistent model volume."""
from __future__ import annotations

import time

import requests
import spacy_pkuseg
import spacy_pkuseg.download as download
from urllib3.exceptions import HTTPError


def download_with_long_timeout(url: str, **kwargs):
    kwargs["timeout"] = (30, 300)
    return requests.get(url, **kwargs)


def main() -> None:
    # Keep the package's checksum verification and extraction behavior.
    # Only this warm-up process overrides its five-second request timeout.
    download.urlopen = download_with_long_timeout
    for attempt in range(1, 4):
        try:
            spacy_pkuseg.pkuseg()
            print(f"Tokenizer ready; persistent cache: {spacy_pkuseg.config.pkuseg_home}")
            return
        except (requests.RequestException, HTTPError, OSError) as error:
            if attempt == 3:
                raise
            print(f"Tokenizer download attempt {attempt} failed: {error}; retrying.", flush=True)
            time.sleep(5)


if __name__ == "__main__":
    main()
