import argparse
import os
import struct
import io
from PIL import Image

class PrsMetaData:
    def __init__(self, width, height, bpp, flag, packed_size=0):
        self.width = width
        self.height = height
        self.bpp = bpp
        self.flag = flag
        self.packed_size = packed_size

class PrsWriter:
    def __init__(self, image, flag):
        self.metadata = PrsMetaData(image.width, image.height, image.mode == 'RGBA' and 32 or 24, flag)
        self.depth = self.metadata.bpp // 8
        self.stride = self.metadata.width * self.depth
        self.input = bytearray(self.stride * self.metadata.height)
        self.output = io.BytesIO()
        self.hash_table = {}

        # Convert image to byte array
        pixels = image.load()
        for y in range(image.height):
            for x in range(image.width):
                idx = (y * self.stride) + (x * self.depth)
                pixel = pixels[x, y]
                self.input[idx:idx+3] = pixel[:3][::-1]  # BGR order
                if self.depth == 4:
                    self.input[idx+3] = pixel[3]

        if (self.metadata.flag & 0x80) != 0:
            for i in range(len(self.input) - 1, self.depth - 1, -1):
                self.input[i] = (self.input[i] - self.input[i - self.depth] + 256) % 256

    def hash(self, index):
        if index + 3 > len(self.input):
            return 0
        return self.input[index] | (self.input[index + 1] << 8) | (self.input[index + 2] << 16)

    def pack(self):
        # Write header
        self.output.write(b'YB')
        self.output.write(bytes([self.metadata.flag, self.metadata.bpp // 8]))
        self.output.write(b'\x00' * 8)  # Placeholder for packed size
        self.output.write(struct.pack('<HH', self.metadata.width, self.metadata.height))

        input_index = 0
        control = 0
        mask = 1
        control_buffer = bytearray()

        while input_index < len(self.input):
            match_length, match_offset = self.find_longest_match(input_index)

            if match_length < 3:
                # Literal byte
                control_buffer.append(self.input[input_index])
                input_index += 1
            else:
                # Set control bit
                control |= mask

                if match_length <= 5 and match_offset <= 256:
                    # Short match
                    encoded_value = ((match_offset - 1) | ((match_length - 2) << 6)) & 0xFF
                    control_buffer.append(encoded_value)
                else:
                    # Long match
                    encoded_offset = match_offset - 1
                    if match_length <= 9:
                        encoded_length = match_length - 2
                        control_buffer.append(0x80 | (encoded_offset >> 5))
                        control_buffer.append((encoded_offset & 0x1F) | (encoded_length << 5))
                    else:
                        control_buffer.append(0xC0 | (encoded_offset >> 8))
                        control_buffer.append(encoded_offset & 0xFF)
                        control_buffer.append(match_length - 1)

                input_index += match_length

            mask <<= 1
            if mask == 0x100:
                self.output.write(bytes([control]))
                self.output.write(control_buffer)
                control_buffer = bytearray()
                control = 0
                mask = 1

            # Update hash table
            for i in range(input_index - match_length, input_index):
                h = self.hash(i)
                if h not in self.hash_table:
                    self.hash_table[h] = []
                self.hash_table[h].append(i)

        # Write final control byte and buffer if necessary
        if mask != 1 or control_buffer:
            self.output.write(bytes([control]))
            self.output.write(control_buffer)

        # Update packed size in header
        self.metadata.packed_size = self.output.tell() - 16
        self.output.seek(4)
        self.output.write(struct.pack('<I', self.metadata.packed_size))

    def find_longest_match(self, input_index):
        match_length = 0
        match_offset = 0

        h = self.hash(input_index)
        if h not in self.hash_table:
            return match_length, match_offset

        max_offset = min(input_index, 0x2000)
        max_length = min(len(self.input) - input_index, 0x100)

        for offset in self.hash_table[h]:
            if input_index - offset > max_offset:
                continue

            length = 0
            while length < max_length and self.input[input_index + length] == self.input[offset + length]:
                length += 1

            if length > match_length:
                match_length = length
                match_offset = input_index - offset
                if match_length == max_length:
                    break

        if match_length < 3:
            match_length = 0
            match_offset = 0

        return match_length, match_offset

    def save_to_file(self, file_path):
        with open(file_path, 'wb') as f:
            f.write(self.output.getvalue())

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

def read_prs_meta_data(file_path):
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

def convert_bmp_to_prs(input_path, output_path):
    with Image.open(input_path) as image:
        flag = 0x80 if image.mode == 'RGBA' else 0x00
        writer = PrsWriter(image, flag)
        writer.pack()
        writer.save_to_file(output_path)

def convert_prs_to_bmp(input_path, output_path):
    meta_data = read_prs_meta_data(input_path)
    reader = PrsReader(input_path, meta_data)
    reader.unpack()
    reader.save_as_bmp(output_path)

def process_directory(input_dir, output_dir, conversion_type):
    os.makedirs(output_dir, exist_ok=True)
    for filename in os.listdir(input_dir):
        input_file = os.path.join(input_dir, filename)
        if conversion_type == 'bmp2prs' and filename.lower().endswith('.bmp'):
            output_file = os.path.join(output_dir, os.path.splitext(filename)[0] + '.prs')
            convert_bmp_to_prs(input_file, output_file)
            print(f"Converted: {input_file} -> {output_file}")
        elif conversion_type == 'prs2bmp' and filename.lower().endswith('.prs'):
            output_file = os.path.join(output_dir, os.path.splitext(filename)[0] + '.bmp')
            convert_prs_to_bmp(input_file, output_file)
            print(f"Converted: {input_file} -> {output_file}")

def main():
    parser = argparse.ArgumentParser(description="Convert between BMP and PRS formats.")
    parser.add_argument('conversion_type', choices=['bmp2prs', 'prs2bmp'], help="Conversion direction")
    parser.add_argument('input', help="Input file or directory")
    parser.add_argument('output', help="Output file or directory")
    args = parser.parse_args()

    if os.path.isdir(args.input):
        process_directory(args.input, args.output, args.conversion_type)
    else:
        if args.conversion_type == 'bmp2prs':
            convert_bmp_to_prs(args.input, args.output)
        else:
            convert_prs_to_bmp(args.input, args.output)
        print(f"Converted: {args.input} -> {args.output}")

if __name__ == "__main__":
    main()
