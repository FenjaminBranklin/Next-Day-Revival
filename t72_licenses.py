"""Collect license notices for the frozen T-72 extractor.

The release workflow runs this in the same Python environment that builds the
executable. Extra notices are harmless; a missing runtime notice is not.
"""

import argparse
import importlib.metadata
import os
import shutil
import sys


DISTRIBUTIONS = (
    "PyInstaller", "pyinstaller-hooks-contrib", "altgraph", "packaging",
    "pefile", "pywin32-ctypes", "setuptools", "UnityPy", "numpy", "Pillow",
    "lz4", "brotli", "texture2ddecoder", "etcpak", "fsspec", "attrs",
    "tpk_ar",
)


def notice_file(path):
    name = os.path.basename(str(path)).upper()
    return (name.startswith("LICENSE") or name.startswith("COPYING")
            or name.startswith("NOTICE") or name.startswith("AUTHORS"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    output = os.path.abspath(args.output)
    os.makedirs(output, exist_ok=True)

    index = ["T-72 extractor third-party notices", ""]
    python_license = os.path.join(sys.base_prefix, "LICENSE.txt")
    if not os.path.isfile(python_license):
        raise SystemExit("Python LICENSE.txt not found at " + python_license)
    shutil.copy2(python_license, os.path.join(output, "PYTHON_LICENSE.txt"))
    index.append("Python %s - PYTHON_LICENSE.txt" % sys.version.split()[0])

    for name in DISTRIBUTIONS:
        dist = importlib.metadata.distribution(name)
        version = dist.version
        directory = os.path.join(output, "%s-%s" % (name, version))
        copied = 0
        for entry in dist.files or ():
            if not notice_file(entry):
                continue
            source = dist.locate_file(entry)
            if not os.path.isfile(source):
                continue
            relative = str(entry).replace("\\", "/")
            target = os.path.join(directory, relative.replace("/", os.sep))
            os.makedirs(os.path.dirname(target), exist_ok=True)
            shutil.copy2(source, target)
            copied += 1
        if copied == 0:
            raise SystemExit("No license notice found for %s %s" % (name, version))
        index.append("%s %s - %d notice file(s)" % (name, version, copied))

    with open(os.path.join(output, "INDEX.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(index) + "\n")
    print("Collected notices for %d distributions." % len(DISTRIBUTIONS))


if __name__ == "__main__":
    main()
