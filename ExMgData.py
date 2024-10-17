#*-* encoding:utf-8*-*
import os
import sys
import json
from struct import unpack
from typing import BinaryIO, Dict, List, Tuple

KEY = '女教師ゆうこ1968'.encode('cp932') # Use Garbro for keys

def unpack_uint32(file: BinaryIO, offset: int) -> int:
    file.seek(offset)
    return unpack('<I', file.read(4))[0]

def xor_decrypt(data: bytes, key: bytes) -> bytes:
    return bytes(data[i] ^ key[i % len(key)] for i in range(len(data)))

def read_entry(file: BinaryIO, base_offset: int, name_offset: int, file_offset: int, size_offset: int) -> Tuple[str, int, int]:
    file.seek(base_offset + name_offset)
    name_bytes = file.read(0x20)  # Read up to 32 bytes for the name
    try:
        name = name_bytes.split(b'\x00', 1)[0].decode('cp932')
    except UnicodeDecodeError:
        return None, 0, 0

    offset = unpack_uint32(file, base_offset + file_offset)
    size = unpack_uint32(file, base_offset + size_offset)
    return name, offset, size

def save_file(file: BinaryIO, output_dir: str, name: str, offset: int, size: int, key: bytes) -> None:
    output_path = os.path.join(output_dir, name)
    file.seek(offset)
    data = xor_decrypt(file.read(size), key)
    with open(output_path, 'wb') as out_file:
        out_file.write(data)
    print(f"Data saved to: {output_path}")

def process_mbl(input_file: str, output_dir: str) -> None:
    data_entries: Dict[str, str] = {}
    parameter_sets = [
        {"entry_size": 0x40, "name_offset": 0x00, "file_offset": 0x38, "size_offset": 0x3C, "key": KEY},
        {"entry_size": 0x18, "name_offset": 0x00, "file_offset": 0x10, "size_offset": 0x14, "key": KEY}
    ]

    with open(input_file, 'rb') as f:
        count = unpack_uint32(f, 0)
        entries: List[Tuple[str, int, int]] = []
        successful_params = None
        
        for params in parameter_sets:
            f.seek(4)  # Reset to start of entries
            entries.clear()
            success = True

            for i in range(count):
                base_offset = 0x04 + i * params["entry_size"]
                entry = read_entry(f, base_offset, params["name_offset"], params["file_offset"], params["size_offset"])
                if entry[0] is None:
                    success = False
                    print(f"Decoding failed with entry size {params['entry_size']}. Trying next parameter set.")
                    break
                entries.append(entry)

            if success:
                print(f"Successfully decoded with entry size {params['entry_size']}")
                successful_params = params
                break
        
        if not successful_params:
            print("Failed to decode with all parameter sets. Exiting.")
            return

        for i, (name, offset, size) in enumerate(entries):
            print(f"Entry {i+1}/{count}:")
            print(f"Name: {name}")
            print(f"Data Offset: {offset}")
            print(f"Size: {size}")
            
            save_file(f, output_dir, name, offset, size, successful_params["key"])
            f.seek(0x04 + i * successful_params["entry_size"])
            additional_bytes = f.read(successful_params["entry_size"])
            data_entries[name] = ''.join(f"{byte:02x}" for byte in additional_bytes)
    
    output_data = {
        "parameters": {
            "entry_size": successful_params["entry_size"],
            "name_offset": successful_params["name_offset"],
            "file_offset": successful_params["file_offset"],
            "size_offset": successful_params["size_offset"],
            "key": successful_params["key"].decode('cp932')
        },
        **data_entries
    }
    
    json_path = os.path.join(output_dir, "entries.json")
    with open(json_path, 'w', encoding='utf-8') as json_file:
        json.dump(output_data, json_file, ensure_ascii=False, indent=4)
    print(f"JSON data saved to: {json_path}")

if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("Usage: python ExMgData.py <input_file> <output_directory>")
        sys.exit(1)
    
    os.makedirs(sys.argv[2], exist_ok=True)
    process_mbl(sys.argv[1], sys.argv[2])
