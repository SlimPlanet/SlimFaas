// Small deterministic ZIP writer (stored entries). No runtime/build dependency needed.
export function createZip(files: { name: string; content: Buffer }[]): Buffer {
    const local: Buffer[] = [];
    const central: Buffer[] = [];
    let offset = 0;
    for (const { name, content } of files) {
        const filename = Buffer.from(name);
        let crc = 0xffffffff;
        for (const byte of content) {
            crc ^= byte;
            for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
        }
        crc = (crc ^ 0xffffffff) >>> 0;
        const header = Buffer.alloc(30);
        header.writeUInt32LE(0x04034b50, 0);
        header.writeUInt16LE(20, 4);
        header.writeUInt16LE(0x800, 6); // UTF-8 filenames
        header.writeUInt16LE(33, 12); // 1980-01-01, for reproducible archives
        header.writeUInt32LE(crc, 14);
        header.writeUInt32LE(content.length, 18);
        header.writeUInt32LE(content.length, 22);
        header.writeUInt16LE(filename.length, 26);
        local.push(header, filename, content);
        const entry = Buffer.alloc(46);
        entry.writeUInt32LE(0x02014b50, 0);
        entry.writeUInt16LE(20, 4);
        entry.writeUInt16LE(20, 6);
        entry.writeUInt16LE(0x800, 8);
        entry.writeUInt16LE(33, 14);
        entry.writeUInt32LE(crc, 16);
        entry.writeUInt32LE(content.length, 20);
        entry.writeUInt32LE(content.length, 24);
        entry.writeUInt16LE(filename.length, 28);
        entry.writeUInt32LE(offset, 42);
        central.push(entry, filename);
        offset += header.length + filename.length + content.length;
    }
    const directory = Buffer.concat(central);
    const end = Buffer.alloc(22);
    end.writeUInt32LE(0x06054b50, 0);
    end.writeUInt16LE(files.length, 8);
    end.writeUInt16LE(files.length, 10);
    end.writeUInt32LE(directory.length, 12);
    end.writeUInt32LE(offset, 16);
    return Buffer.concat([...local, directory, end]);
}
