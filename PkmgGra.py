#Use this file to create patches that is mg_data2.mbl, mg_data3.mbl and so on. IT WILL NOT WORK IF YOU TRY TO CREATE THE ARCHIVE ITSELF AS THE ORIGINAL ARCHIVE HAS SOME HEX DATA THAT IS REQUIRED BUT UNKNOWN HOW IT
#GOT THERE. So just use it to make patches, also you might need to change the name_length depending on the archive. In this case it was 13.

import struct
import sys
import os
import zlib
from io import BytesIO

class Entry:
    def __init__(self, name, entry_type, offset, size):
        # Ensure the name fits in 13 bytes, truncate if necessary
        self.name = name[:13].upper()  # Enforce lowercase and limit to 13 chars
        self.type = entry_type
        self.offset = offset  # Offset will be relative to the start of the data section.
        self.size = size

def write_fixed_length_string(file, string, length):
    """Write a string with fixed length, padding with null bytes."""
    btw = b'\x00PRS'
    file.write(string.encode('utf-8'))
    if length >= 9:
        file.write(btw[:length - len(string)])
    else:
        file.write(b'\x00PRS')
    padding = length - (len(string) + 4)
    if padding > 0:
        file.write(b'\x00' * padding)

def pack_files(file_paths, output_file):
    entries = []
    data_buffer = BytesIO()
    
    entry_size = 0x15  # Each entry is exactly 21 bytes (0x15)
    header_size = 8  # Starting size for count + filename length fields
    data_section_offset = header_size + (entry_size * len(file_paths))  # Data section starts after the header

    offset = 0  # Offset in the data section

    # Create entries and write file data to data_buffer
    for file_path in file_paths:
        filename = os.path.basename(file_path)
        name_without_ext = os.path.splitext(filename)[0].lower()

        # Read the file data
        with open(file_path, 'rb') as f:
            file_data = f.read()

        # Check if the first byte is 0x78, indicating the file is already compressed
        if file_data[0] == 0x78:
            compressed_data = file_data  # Already compressed, no need to compress again
        else:
            compressed_data = file_data
           # compressed_data = zlib.compress(file_data)  # Compress the file data
        
        size = len(compressed_data)

        # Write compressed data to buffer
        entry_offset = offset
        data_buffer.write(compressed_data)
        offset += size  # Update offset for the next file

        # Create a new entry for the archive, respecting the 13-byte name limit
        entry = Entry(name_without_ext, "image", data_section_offset + entry_offset, size)
        entries.append(entry)
    
    # Open the output file for writing
    with open(output_file, 'wb') as archive_file:
        # Write the entry count (4 bytes) and filename length (4 bytes)
        archive_file.write(struct.pack('<I I', len(entries), 13))  # 13 is the fixed filename length

        # Write entries' information, each exactly 21 bytes
        for entry in entries:
            # Write the 13-byte name (padded with nulls if shorter)
            write_fixed_length_string(archive_file, entry.name, 13)

            # Write the 4-byte offset and 4-byte size
            archive_file.write(struct.pack('<I I', entry.offset, entry.size))

        # Write the file data buffer (which contains the compressed file data)
        archive_file.write(data_buffer.getvalue())

if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("PackMbl.py <input_dir> <output_file>")
        sys.exit(1)
    input_dir = sys.argv[1]
    output_file = sys.argv[2]

    # Gather all files from the input directory
    file_paths = [os.path.join(input_dir, f) for f in os.listdir(input_dir) if os.path.isfile(os.path.join(input_dir, f))]

    if not file_paths:
        print(f"No files found in directory: {input_dir}")
        sys.exit(1)

    pack_files(file_paths, output_file)
    print(f"Packed {len(file_paths)} files into {output_file}")
