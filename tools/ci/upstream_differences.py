"""Read the reviewed i386 differences from the parity documentation."""
import json
from pathlib import Path
import re
import sys

UPSTREAM = '410342a82da1b2e8286c290274aeb0f1731a6859'
CATALOG = Path('docs/macos-port/intended-differences.md')


def read_catalog(path):
    blocks = re.findall(r'^\`\`\`json\n(.*?)^\`\`\`$', path.read_text(), re.M | re.S)
    if len(blocks) != 1:
        raise ValueError(f'{path}: expected one JSON catalog')
    catalog = json.loads(blocks[0])
    if catalog['upstream'] != UPSTREAM or not re.fullmatch(r'[0-9a-f]{40}', catalog['reviewed']):
        raise ValueError(f'{path}: invalid upstream or reviewed commit')
    sources, objects = {}, {}
    for entry in catalog['entries']:
        label, source = entry['id'], entry['source']
        if label in sources.values() or source in sources or not re.fullmatch(r'src/[\w./-]+', source):
            raise ValueError(f'{path}: duplicate or invalid entry {label} for {source}')
        if '..' in Path(source).parts:
            raise ValueError(f'{path}: invalid source path {source}')
        sources[source] = label
        for obj in entry.get('objects', []):
            if obj in objects or not re.fullmatch(r'CMakeFiles/cod2_(objs|linux|lnxded)\.dir/' +
                                                   re.escape(source) + r'\.o', obj):
                raise ValueError(f'{path}: duplicate or invalid object {obj} for {label}')
            objects[obj] = label
    return catalog['reviewed'], sources, objects


if __name__ == '__main__':
    try:
        revision, _, objects = read_catalog(Path(sys.argv[1]))
        print(revision, *objects, sep='\n')
    except (KeyError, OSError, ValueError) as error:
        sys.exit(str(error))
