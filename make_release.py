#!/usr/bin/env python3
"""Build the Windows release zip, which runs without .NET, Python or Node installed.

    python make_release.py 1.0.0

Writes release/queens-domain-map-<version>-windows.zip. It holds the scripts and viewer from this
repository, the extractor published as one self-contained exe, the native gltfpack, the embeddable
Python, double-click launchers and the third-party notices. No game content goes into it.
Needs the .NET 9 SDK and network access (downloads are checked against the SHA-256 values below).
"""
import hashlib, json, os, re, shutil, subprocess, sys, urllib.request, zipfile

ROOT = os.path.dirname(os.path.abspath(__file__))
REL = os.path.join(ROOT, 'release')
CACHE = os.path.join(REL, 'cache')

DOWNLOADS = {
    'gltfpack': ('https://github.com/zeux/meshoptimizer/releases/download/v1.3/gltfpack-windows.zip',
                 'f6e9c09d66af23da3da71b86f0652d2413e81c734e0aecd1cd3d0e6e6e8645c0'),
    'python': ('https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip',
               '4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3'),
    'apache': ('https://www.apache.org/licenses/LICENSE-2.0.txt',
               'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30'),
}

MESHOPT_LICENSE = '''MIT License

Copyright (c) 2016-2026 Arseny Kapoulkine

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
'''

OPEN_CMD = r'''@echo off
rem Builds the map the first time (and after game updates), then opens it in your browser.
cd /d "%~dp0"
tools\python\python.exe build.py --auto --open --clean --out map %*
if errorlevel 1 pause
'''

REBUILD_CMD = r'''@echo off
rem Rebuilds the map from the installed game even if it is already up to date, then opens it.
cd /d "%~dp0"
tools\python\python.exe build.py --serve --open --clean --out map %*
if errorlevel 1 pause
'''

README_TXT = '''Queen's Domain map {version}
=========================

An interactive 3D map of Queen's Domain, built on your PC from your own copy of the game.
Nothing else needs to be installed. This is a fan project, not affiliated with the game's developers.

How to use it
-------------
1. Install Queen's Domain through Steam.
2. Unzip this folder anywhere (not inside the game folder).
3. Double-click "Open map.cmd".

The first time, it builds the map from the game files. That takes 10 to 20 minutes and needs about
1 GB of free space while it runs (the finished map is about 130 MB, in the "map" folder).
When it's done the map opens in your browser. Keep the black window open while you use the map;
closing it stops the map.

After that, "Open map.cmd" opens the map right away. If the game has been updated it rebuilds first.
"Rebuild map.cmd" forces a rebuild.

If Windows shows "Windows protected your PC", click "More info" and then "Run anyway". The tools in
this folder are not code-signed.

If it can't find the game
-------------------------
It looks in your Steam libraries. If the game is somewhere else, the window asks you for the game folder
(the one with QueensDomain.exe). You can also pass it directly from a command prompt:

    "Open map.cmd" --game "D:\\Games\\QueensDomain"

Source code, bug reports and the full documentation:
https://github.com/{repo}
'''


def fail(msg):
    sys.exit('error: ' + msg)


def fetch(key):
    url, sha = DOWNLOADS[key]
    path = os.path.join(CACHE, key, url.rsplit('/', 1)[1])
    if not os.path.isfile(path):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        print(f'downloading {url}')
        with urllib.request.urlopen(url) as r, open(path + '.part', 'wb') as f:
            shutil.copyfileobj(r, f)
        os.replace(path + '.part', path)
    got = hashlib.sha256(open(path, 'rb').read()).hexdigest()
    if got != sha:
        os.remove(path)
        fail(f'{url} has SHA-256 {got}, expected {sha}')
    return path


def publish_extractor(dst):
    print('publishing extractor')
    pub = os.path.join(REL, 'publish')
    shutil.rmtree(pub, ignore_errors=True)
    subprocess.run(['dotnet', 'publish', os.path.join(ROOT, 'Extractor.csproj'), '-c', 'Release', '-r', 'win-x64',
                    '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
                    '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none', '-o', pub, '--nologo', '-v', 'q'],
                   check=True)
    shutil.copy2(os.path.join(pub, 'Extractor.exe'), dst)
    # obj/ now holds the win-x64 restore; the deps file there lists every package that went into the exe.
    return os.path.join(ROOT, 'obj', 'project.assets.json')


def notices(assets_path, apache_text, python_dir):
    """THIRD-PARTY-NOTICES.txt: every bundled component with its license."""
    nuget = os.path.join(os.path.expanduser('~'), '.nuget', 'packages')
    assets = json.load(open(assets_path, encoding='utf-8'))
    target = next(k for k in assets['targets'] if k.endswith('/win-x64'))
    parts = ['Third-party software in this package\n' + '=' * 37 + '\n',
             'Queen\'s Domain map itself is MIT licensed (see LICENSE). It bundles the components below.\n'
             'Game content the build extracts belongs to the game\'s developers and is not part of this package.\n']
    need_apache = False
    for key, info in sorted(assets['targets'][target].items(), key=lambda kv: kv[0].lower()):
        if info.get('type') != 'package':
            continue
        name, ver = key.split('/')
        pkg = os.path.join(nuget, name.lower(), ver.lower())
        nuspec = open(os.path.join(pkg, name.lower() + '.nuspec'), encoding='utf-8-sig').read()
        lic = re.search(r'<license type="expression">([^<]+)</license>', nuspec)
        lic_url = re.search(r'<licenseUrl>([^<]+)</licenseUrl>', nuspec)
        proj = re.search(r'<projectUrl>([^<]+)</projectUrl>', nuspec)
        copy = re.search(r'<copyright>([^<]+)</copyright>', nuspec)
        head = f'\n{"-" * 72}\n{name} {ver}\n'
        head += f'License: {lic.group(1) if lic else (lic_url.group(1) if lic_url else "see below")}\n'
        if proj:
            head += f'Project: {proj.group(1)}\n'
        if copy:
            head += f'{copy.group(1)}\n'
        texts = []
        for fname in sorted(os.listdir(pkg)):
            if re.match(r'(license|licence|third-party-notices|thirdpartynotices)', fname, re.I) and os.path.isfile(os.path.join(pkg, fname)):
                texts.append(open(os.path.join(pkg, fname), encoding='utf-8', errors='replace').read().strip())
        if lic and 'Apache' in lic.group(1) and not texts:
            need_apache = True
            texts.append('(Apache License 2.0, full text at the end of this file)')
        parts.append(head + ('\n' + '\n\n'.join(texts) + '\n' if texts else ''))

    dotnet_root = os.path.dirname(shutil.which('dotnet'))
    parts.append(f'\n{"-" * 72}\n.NET runtime (bundled inside Extractor.exe)\nLicense: MIT\nProject: https://github.com/dotnet/runtime\n\n'
                 + open(os.path.join(dotnet_root, 'LICENSE.txt'), encoding='utf-8', errors='replace').read().strip()
                 + '\n\n' + open(os.path.join(dotnet_root, 'ThirdPartyNotices.txt'), encoding='utf-8', errors='replace').read().strip() + '\n')
    parts.append(f'\n{"-" * 72}\ngltfpack and meshoptimizer (tools/gltfpack.exe, web/vendor/meshopt_decoder.module.js)\n'
                 'Project: https://github.com/zeux/meshoptimizer\n\n' + MESHOPT_LICENSE)
    parts.append(f'\n{"-" * 72}\nthree.js (web/vendor/)\nProject: https://github.com/mrdoob/three.js\n\n'
                 + open(os.path.join(ROOT, 'web', 'vendor', 'three-LICENSE.txt'), encoding='utf-8').read().strip() + '\n')
    parts.append(f'\n{"-" * 72}\nPython 3.12 embeddable package (tools/python/)\nProject: https://www.python.org/\n\n'
                 + open(os.path.join(python_dir, 'LICENSE.txt'), encoding='utf-8', errors='replace').read().strip() + '\n')
    if need_apache:
        parts.append(f'\n{"-" * 72}\nApache License 2.0\n\n' + apache_text.strip() + '\n')
    return ''.join(parts)


def repo_slug():
    try:
        url = subprocess.run(['git', '-C', ROOT, 'remote', 'get-url', 'origin'], capture_output=True, text=True).stdout.strip()
    except OSError:
        return 'queens-domain-map'
    url = url.removesuffix('.git')
    return url.split('github.com', 1)[1].lstrip(':/') if 'github.com' in url else 'queens-domain-map'


def main():
    if len(sys.argv) != 2:
        sys.exit('usage: python make_release.py <version>')
    version = sys.argv[1].lstrip('v')
    name = f'queens-domain-map-{version}-windows'
    stage = os.path.join(REL, name)
    shutil.rmtree(stage, ignore_errors=True)
    tools = os.path.join(stage, 'tools')
    os.makedirs(tools)

    for f in ('build.py', 'build_data.py', 'stamp_version.py', 'LICENSE'):
        shutil.copy2(os.path.join(ROOT, f), stage)
    shutil.copytree(os.path.join(ROOT, 'web'), os.path.join(stage, 'web'))

    assets_path = publish_extractor(tools)

    with zipfile.ZipFile(fetch('gltfpack')) as z:
        with z.open('gltfpack.exe') as src, open(os.path.join(tools, 'gltfpack.exe'), 'wb') as dst:
            shutil.copyfileobj(src, dst)
    python_dir = os.path.join(tools, 'python')
    with zipfile.ZipFile(fetch('python')) as z:
        z.extractall(python_dir)
    apache_text = open(fetch('apache'), encoding='utf-8').read()

    for fname, text in (('Open map.cmd', OPEN_CMD), ('Rebuild map.cmd', REBUILD_CMD)):
        with open(os.path.join(stage, fname), 'w', encoding='ascii', newline='\r\n') as f:
            f.write(text)
    with open(os.path.join(stage, 'README.txt'), 'w', encoding='utf-8', newline='\r\n') as f:
        f.write(README_TXT.format(version=version, repo=repo_slug()))
    with open(os.path.join(stage, 'THIRD-PARTY-NOTICES.txt'), 'w', encoding='utf-8', newline='\r\n') as f:
        f.write(notices(assets_path, apache_text, python_dir))

    zip_path = os.path.join(REL, name + '.zip')
    if os.path.exists(zip_path):
        os.remove(zip_path)
    with zipfile.ZipFile(zip_path, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for dirpath, _, files in os.walk(stage):
            for fname in sorted(files):
                full = os.path.join(dirpath, fname)
                z.write(full, os.path.join(name, os.path.relpath(full, stage)))
    print(f'{zip_path}  {os.path.getsize(zip_path) / 1e6:.1f} MB')


if __name__ == '__main__':
    main()
