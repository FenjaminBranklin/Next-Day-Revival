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


def flat_name(relative, taken):
    """One flat file name per notice. The nested path a wheel gives its
    vendored notices is dropped; a collision gets a counter, so nothing is
    overwritten and nothing is lost."""
    base = os.path.basename(relative)
    stem, extension = os.path.splitext(base)
    candidate = base
    counter = 2
    while candidate.lower() in taken:
        candidate = "%s_%d%s" % (stem, counter, extension)
        counter += 1
    return candidate


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    output = os.path.abspath(args.output)
    os.makedirs(output, exist_ok=True)

    index = ["T-72 extractor third-party notices", "",
             "Notice files keep their own name, not the nested path they had",
             "inside the wheel - a vendored tree pushes the unpacked package",
             "past the 260 character Windows path limit and the launcher then",
             "cannot unpack the release. The original path of every file is",
             "recorded below, so the notice can still be traced to its source.",
             ""]
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
        taken = set()
        notes = []
        for entry in dist.files or ():
            if not notice_file(entry):
                continue
            source = dist.locate_file(entry)
            if not os.path.isfile(source):
                continue
            relative = str(entry).replace("\\", "/")
            flat = flat_name(relative, taken)
            taken.add(flat.lower())
            target = os.path.join(directory, flat)
            os.makedirs(os.path.dirname(target), exist_ok=True)
            shutil.copy2(source, target)
            notes.append("    %s <- %s" % (flat, relative))
            copied += 1
        if copied == 0:
            raise SystemExit("No license notice found for %s %s" % (name, version))
        index.append("%s %s - %d notice file(s)" % (name, version, copied))
        index.extend(notes)

    with open(os.path.join(output, "INDEX.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(index) + "\n")
    print("Collected notices for %d distributions." % len(DISTRIBUTIONS))


if __name__ == "__main__":
    main()
