import argparse
import os
import struct
from PIL import Image


class PrsMetaData:
    def __init__(self, width, height, bpp, flag, packed_size):
        self.width = width
        self.height = height
        self.bpp = bpp
        self.flag = flag
        self.packed_size = packed_size


class PrsReader:
    length_table = [i + 3 for i in range(0xfe)] + [0x400, 0x1000]

    def __init__(self, file_path, meta_data):
        self.file_path = file_path
        self.width = meta_data.width
        self.height = meta_data.height
        self.depth = meta_data.bpp // 8
        self.flag = meta_data.flag
        self.packed_size = meta_data.packed_size
        self.output = bytearray(self.width * self.height * self.depth)
        self.format = 'RGBA' if self.depth == 4 else 'RGB'

    def unpack(self):
        with open(self.file_path, 'rb') as f:
            f.seek(0x10)
            remaining = self.packed_size
            dst = 0
            bit = 0
            ctl = 0

            while remaining > 0 and dst < len(self.output):
                bit >>= 1
                if bit == 0:
                    ctl = f.read(1)[0]
                    remaining -= 1
                    bit = 0x80

                if remaining <= 0:
                    break

                if ctl & bit == 0:
                    self.output[dst] = f.read(1)[0]
                    dst += 1
                    remaining -= 1
                    continue

                b = f.read(1)[0]
                remaining -= 1
                length = 0
                shift = 0

                if b & 0x80:
                    if remaining <= 0:
                        break
                    shift = f.read(1)[0]
                    remaining -= 1
                    shift |= (b & 0x3f) << 8

                    if b & 0x40:
                        if remaining <= 0:
                            break
                        offset = f.read(1)[0]
                        remaining -= 1
                        length = self.length_table[offset]
                    else:
                        length = (shift & 0xf) + 3
                        shift >>= 4
                else:
                    length = b >> 2
                    b &= 3
                    if b == 3:
                        length += 9
                        read_data = f.read(length)
                        self.output[dst:dst + length] = read_data
                        dst += len(read_data)
                        remaining -= len(read_data)
                        continue
                    shift = length
                    length = b + 2

                shift += 1
                if dst < shift:
                    raise ValueError("Invalid offset value")
                length = min(length, len(self.output) - dst)
                for i in range(length):
                    self.output[dst + i] = self.output[dst - shift + i]
                dst += length

            if self.flag & 0x80:
                for i in range(self.depth, len(self.output)):
                    self.output[i] = (self.output[i] + self.output[i - self.depth]) % 256

            if self.depth == 4 and self.is_dummy_alpha_channel():
                self.format = 'RGB'

    def is_dummy_alpha_channel(self):
        alpha = self.output[3]
        if alpha == 0xFF:
            return False
        for i in range(7, len(self.output), 4):
            if self.output[i] != alpha:
                return False
        return True

    def save_as_bmp(self, output_path):
            mode = 'RGBA' if self.depth == 4 else 'RGB'
            image = Image.frombytes(mode, (self.width, self.height), bytes(self.output))
            
            # Swap channels from BGR to RGB
            if self.depth == 3:
                # Convert BGR to RGB for RGB images
                r, g, b = image.split()
                image = Image.merge("RGB", (b, g, r))
            elif self.depth == 4:
                # Convert BGRA to RGBA for images with alpha channel
                r, g, b, a = image.split()
                image = Image.merge("RGBA", (b, g, r, a))

            # Convert image to RGB if alpha channel is dummy
            if mode == 'RGBA' and self.is_dummy_alpha_channel():
                image = image.convert('RGB')
            
            image.save(output_path, 'BMP')


def read_meta_data(file_path):
    with open(file_path, 'rb') as f:
        header = f.read(16)
        if header[:2] != b'YB':
            raise ValueError("Not a valid PRS file")
        bpp = header[3]
        if bpp not in (3, 4):
            raise ValueError("Unsupported BPP value")
        width, height = struct.unpack_from('<HH', header, 12)
        flag = header[2]
        packed_size = struct.unpack_from('<I', header, 4)[0]
        return PrsMetaData(width, height, bpp * 8, flag, packed_size)


def prs_to_bmp(input_path, output_path):
    meta_data = read_meta_data(input_path)
    reader = PrsReader(input_path, meta_data)
    reader.unpack()
    reader.save_as_bmp(output_path)


def process_directory(input_dir, output_dir):
    # Ensure the output directory exists
    os.makedirs(output_dir, exist_ok=True)

    # Iterate through all files in the input directory
    for filename in os.listdir(input_dir):
        if filename.lower().endswith('.prs'):
            input_file = os.path.join(input_dir, filename)
            output_file = os.path.join(output_dir, os.path.splitext(filename)[0] + '.bmp')
            try:
                prs_to_bmp(input_file, output_file)
                print(f"Converted: {input_file} -> {output_file}")
            except Exception as e:
                print(f"Failed to convert {input_file}: {e}")


def main():
    parser = argparse.ArgumentParser(description="Convert PRS to BMP.")
    parser.add_argument('input', help="Input PRS file or directory")
    parser.add_argument('output', help="Output BMP file or directory")
    args = parser.parse_args()

    # Check if input is a file or a directory
    if os.path.isdir(args.input):
        process_directory(args.input, args.output)
    else:
        prs_to_bmp(args.input, args.output)


if __name__ == "__main__":
    main()
