import struct
import sys
import os
import zlib
from io import BytesIO

class Entry:
    def __init__(self, name, entry_type, offset, size):
        self.name = name
        self.type = entry_type
        self.offset = offset
        self.size = size

    def check_placement(self, max_offset):
        return self.offset + self.size <= max_offset

class ArcFile:
    def __init__(self, file_data, entries):
        self.file_data = file_data
        self.entries = entries

    def open_entry(self, entry):
        self.file_data.seek(entry.offset)
        return self.file_data.read(entry.size)

def read_string(file, length):
    raw_string = file.read(length)
    return raw_string.split(b'\x00', 1)[0].decode('utf-8').lower()

def is_sane_count(count):
    return 0 < count < 10000  # Adjust this sanity check based on expected file size

def try_open_gra_mbl(file_path):
    with open(file_path, 'rb') as f:
        file_data = BytesIO(f.read())

    # Read filename length and entry count
    file_data.seek(0)
    count,filename_len = struct.unpack('<I I', file_data.read(8))

    if filename_len < 8 or filename_len > 0x40 or not is_sane_count(count):
        return None

    # Verify the file name to match the archive type
    arc_name = os.path.splitext(os.path.basename(file_path))[0]
    if arc_name.lower() != "mg_gra":
        return None

    index_offset = 8
    entries = []

    # Read index entries
    for _ in range(count):
        file_data.seek(index_offset)
        name = read_string(file_data, filename_len)
        index_offset += filename_len

        offset, size = struct.unpack('<I I', file_data.read(8))
        entry = Entry(name + ".prs", "image", offset, size)

        if not entry.check_placement(len(file_data.getvalue())):
            return None

        entries.append(entry)
        index_offset += 8

    if not entries or (len(entries) == 1 and count > 1):
        return None

    return ArcFile(file_data, entries)

def extract_mbl(file_path, output_dir):
    arc_file = try_open_gra_mbl(file_path)
    if not arc_file:
        print(f"Failed to open archive: {file_path}")
        return

    for entry in arc_file.entries:
        data = arc_file.open_entry(entry)
        
        # If the first byte is 0x78, it's compressed with zlib
        if data[0] == 0x78:
            data = zlib.decompress(data)

        output_file = os.path.join(output_dir, entry.name)
        with open(output_file, 'wb') as f:
            f.write(data)
        print(f"Extracted: {output_file}")


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("ExGraMbl.py <input_file> <output_dir>")
        sys.exit(1)
    file_path = sys.argv[1]
    output_dir = sys.argv[2]
    os.makedirs(output_dir, exist_ok=True)
    extract_mbl(file_path, output_dir)
