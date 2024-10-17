import os
import sys
import json
from struct import pack

def xor_encrypt(data: bytes, key: bytes) -> bytes:
    """XOR encrypt the data using the given key."""
    return bytes([data[i] ^ key[i % len(key)] for i in range(len(data))])

def hex_to_bytes(hex_string: str) -> bytes:
    """Convert a hex string to bytes."""
    return bytes.fromhex(hex_string)

def pack_files(input_dir: str, output_file: str, json_file: str, patch: str):
    """Pack available files using names, data, and parameters from the JSON file into a single archive."""
    with open(json_file, 'r', encoding='utf-8') as jf:
        json_data = json.load(jf)
    
    parameters = json_data['parameters']
    file_info = {k: v for k, v in json_data.items() if k != 'parameters'}
    
    KEY = parameters['key'].encode('cp932')
    ENTRY_SIZE = parameters['entry_size']
    NAME_OFFSET = parameters['name_offset']
    file_offset = parameters['file_offset']
    SIZE_OFFSET = parameters['size_offset']

    # Filter out missing files
    available_files = {k: v for k, v in file_info.items() if os.path.isfile(os.path.join(input_dir, k))}
    file_count = len(available_files)
    
    header_size = 4 + file_count * ENTRY_SIZE  # 4 bytes for count + ENTRY_SIZE bytes for each file entry
    current_offset = header_size + 4  # Adjust for the additional 4 null bytes

    with open(output_file, 'wb') as archive:
        archive.write(pack('<I', file_count))
        
        # Prepare the header
        for i, (file_name, hex_data) in enumerate(available_files.items()):
            file_path = os.path.join(input_dir, file_name)
            
            with open(file_path, 'rb') as f:
                data = f.read()
            
            encrypted_data = xor_encrypt(data, KEY)
            file_size = len(encrypted_data)
            
            entry_start = 4 + i * ENTRY_SIZE
            archive.seek(entry_start)
            
            # Write filename
            name_encoded = file_name.encode('cp932')
            archive.write(name_encoded)
            archive.write(b'\x00' * (NAME_OFFSET + ENTRY_SIZE - len(name_encoded)))
            
            # Write additional data from JSON unless patch mode is enabled
            if patch.lower() != 'yes':
                binary_data = hex_to_bytes(hex_data)
                archive.seek(entry_start)
                archive.write(binary_data[:ENTRY_SIZE])
            
            # Write offset and size
            archive.seek(entry_start + file_offset)
            archive.write(pack('<I', current_offset))
            archive.seek(entry_start + SIZE_OFFSET)
            archive.write(pack('<I', file_size))
            
            current_offset += file_size

        # Write four null bytes at the end of the header
        archive.seek(header_size)
        archive.write(b'\x00\x00\x00\x00')

        # Write file data
        archive.seek(header_size + 4)  # Move past the 4 null bytes
        for file_name in available_files.keys():
            file_path = os.path.join(input_dir, file_name)
            with open(file_path, 'rb') as f:
                data = f.read()
            encrypted_data = xor_encrypt(data, KEY)
            archive.write(encrypted_data)

    print(f"Packed {file_count} files into {output_file}")

if __name__ == "__main__":
    if len(sys.argv) < 5:
        print("Usage: python PkMgData.py <input_directory> <output_archive> <json_file> <patch (yes|no)>")
        sys.exit(1)
    input_directory = sys.argv[1]
    output_archive = sys.argv[2]
    json_file = sys.argv[3]
    patch_mode = sys.argv[4]
    pack_files(input_directory, output_archive, json_file, patch_mode)
