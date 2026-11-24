"""Builds wwwroot/katex.css from a KaTeX release: its dist/katex.min.css with each font put inside the file (woff2,
base64), so the page needs one stylesheet and no font files of its own.

    python tools/katex-css.py <unpacked katex package>/dist wwwroot/katex.css

The package is the npm tarball (https://registry.npmjs.org/katex/-/katex-<version>.tgz); its dist/katex.min.js must
be the same file as wwwroot/katex.js, which this checks.
"""
import base64
import hashlib
import os
import re
import sys

dist, out = sys.argv[1], sys.argv[2]
here = os.path.dirname(os.path.abspath(out))


def digest(path):
    return hashlib.sha256(open(path, 'rb').read()).hexdigest()


if digest(os.path.join(dist, 'katex.min.js')) != digest(os.path.join(here, 'katex.js')):
    sys.exit('dist/katex.min.js is not the katex.js beside the output: not the same release')
css = open(os.path.join(dist, 'katex.min.css'), encoding='utf-8').read()
fonts = 0


def inline(match):
    global fonts
    name = re.search(r'url\(fonts/([^)]+\.woff2)\)', match.group(0)).group(1)
    data = base64.b64encode(open(os.path.join(dist, 'fonts', name), 'rb').read()).decode('ascii')
    fonts += 1
    return 'src:url(data:font/woff2;base64,' + data + ') format("woff2")'


css = re.sub(r'src:url\(fonts/[^}]*?(?=[;}])', inline, css)
if 'url(fonts/' in css or fonts == 0:
    sys.exit('a font was left pointing at a file')
open(out, 'w', encoding='utf-8', newline='').write(css)
print(f'{out}: {fonts} fonts inside, {len(css)} characters')
