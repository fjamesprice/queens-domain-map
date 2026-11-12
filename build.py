#!/usr/bin/env python3
"""Build the Queen's Domain map from your installed copy of the game.

    python build.py                     find the game through Steam, write the map to ./dist
    python build.py --serve             same, then serve it at http://localhost:8000
    python build.py --game "D:\\Games\\QueensDomain" --out C:\\some\\site

Needs the .NET 9 SDK, Python 3.9+ and Node.js 18+. A full build takes about 10 minutes and
leaves about 450 MB of intermediate files in ./out and 130 MB of map in the output folder.
"""
import argparse, functools, http.server, json, os, re, shutil, subprocess, sys, time
from concurrent.futures import ThreadPoolExecutor

ROOT = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(ROOT, 'out')
TOOLS = os.path.join(ROOT, 'tools')
WEB = os.path.join(ROOT, 'web')
DLL = os.path.join(ROOT, 'bin', 'Release', 'net9.0', 'Extractor.dll')
# The Windows release ships these prebuilt; a source checkout builds the extractor and gets gltfpack from npm.
EXTRACTOR_EXE = os.path.join(TOOLS, 'Extractor.exe')
GLTFPACK_EXE = os.path.join(TOOLS, 'gltfpack.exe')
BUILD_STAMP = '.game_build'  # in <site>/data: the game build the map was made from
STEAM_APP_ID = '1799840'
PAKS_REL = os.path.join('QueensDomain', 'Content', 'Paks')
GLTFPACK_VERSION = '1.3.0'
GLTFPACK_COMMON = ['-cc', '-vpf', '-noq', '-km', '-kv']
ENEMY_TRIANGLES = 3000  # enemy models are simplified to roughly this many triangles


def fail(msg):
    sys.exit('error: ' + msg)


def need(tool, hint):
    path = shutil.which(tool)
    if not path:
        fail(f'{tool} not found on PATH. {hint}')
    return path


# ---- finding the game -------------------------------------------------------------------------

def steam_roots():
    roots = []
    if os.name == 'nt':
        import winreg
        for hive, key, value in ((winreg.HKEY_CURRENT_USER, r'Software\Valve\Steam', 'SteamPath'),
                                 (winreg.HKEY_LOCAL_MACHINE, r'SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath')):
            try:
                with winreg.OpenKey(hive, key) as k:
                    roots.append(winreg.QueryValueEx(k, value)[0])
            except OSError:
                pass
        roots.append(r'C:\Program Files (x86)\Steam')
    else:
        home = os.path.expanduser('~')
        roots += [os.path.join(home, '.steam', 'steam'),
                  os.path.join(home, '.local', 'share', 'Steam'),
                  os.path.join(home, '.var', 'app', 'com.valvesoftware.Steam', '.local', 'share', 'Steam'),
                  os.path.join(home, 'Library', 'Application Support', 'Steam')]
    return roots


def find_game():
    """The game's install folder (the one holding QueensDomain.exe), from Steam's library list."""
    libraries = []
    for root in steam_roots():
        libraries.append(root)
        vdf = os.path.join(root, 'steamapps', 'libraryfolders.vdf')
        if os.path.isfile(vdf):
            text = open(vdf, encoding='utf-8', errors='replace').read()
            libraries += [p.replace('\\\\', '\\') for p in re.findall(r'"path"\s+"([^"]+)"', text)]
    for lib in dict.fromkeys(os.path.normpath(p) for p in libraries):
        steamapps = os.path.join(lib, 'steamapps')
        installdir = 'QueensDomain'
        manifest = os.path.join(steamapps, f'appmanifest_{STEAM_APP_ID}.acf')
        if os.path.isfile(manifest):
            m = re.search(r'"installdir"\s+"([^"]+)"', open(manifest, encoding='utf-8', errors='replace').read())
            if m:
                installdir = m.group(1)
        game = os.path.join(steamapps, 'common', installdir)
        if os.path.isdir(os.path.join(game, PAKS_REL)):
            return game
    return None


def resolve_game(arg):
    """Accept the install folder or the Paks folder itself. Returns (install folder, Paks folder)."""
    if arg:
        path = os.path.abspath(arg)
        if os.path.isdir(path) and any(f.endswith('.pak') for f in os.listdir(path)):
            return os.path.normpath(os.path.join(path, '..', '..', '..')), path
        if os.path.isdir(os.path.join(path, PAKS_REL)):
            return path, os.path.join(path, PAKS_REL)
        fail(f'no game files in {path}. Pass the folder that holds QueensDomain.exe.')
    game = find_game()
    if not game and sys.stdin.isatty():
        print('Could not find Queen\'s Domain in your Steam libraries.')
        typed = input('Paste the game folder (the one with QueensDomain.exe) and press Enter: ').strip().strip('"')
        if typed:
            return resolve_game(typed)
    if not game:
        fail('could not find Queen\'s Domain in any Steam library. Pass --game "<folder with QueensDomain.exe>".')
    return game, os.path.join(game, PAKS_REL)


def game_build(game):
    version_file = os.path.join(game, 'version.txt')
    if os.path.isfile(version_file):
        return open(version_file, encoding='utf-8', errors='replace').read().strip()
    return None


# ---- steps ------------------------------------------------------------------------------------

def header(title):
    print(f'== {title}', flush=True)


def extractor(env, step, *extra, show='last'):
    """Run one extractor command. Its full output goes to out/logs/<step>.log."""
    t0 = time.time()
    os.makedirs(os.path.join(OUT, 'logs'), exist_ok=True)
    log_path = os.path.join(OUT, 'logs', step + '.log')
    lines = []
    with open(log_path, 'w', encoding='utf-8') as log:
        exe = [EXTRACTOR_EXE] if os.path.isfile(EXTRACTOR_EXE) else ['dotnet', DLL]
        proc = subprocess.Popen([*exe, step, OUT, *extra], env=env, stdout=subprocess.PIPE,
                                stderr=subprocess.STDOUT, text=True, encoding='utf-8', errors='replace')
        for line in proc.stdout:
            log.write(line)
            line = line.rstrip()
            if line.startswith('loc:'):
                continue  # missing localization entries; harmless
            lines.append(line)
            if show == 'all':
                print('  ' + line, flush=True)
        proc.wait()
    if proc.returncode != 0:
        print('\n'.join(lines[-20:]))
        fail(f'extractor step "{step}" failed (exit {proc.returncode}). Full log: {log_path}')
    if show == 'last' and lines:
        print('  ' + lines[-1])
    print(f'  {time.time() - t0:.0f}s', flush=True)


def gltfpack_command():
    if os.path.isfile(GLTFPACK_EXE):
        return [GLTFPACK_EXE]
    node = need('node', 'Install Node.js 18 or newer from https://nodejs.org/.')
    cli = os.path.join(TOOLS, 'node_modules', 'gltfpack', 'cli.js')
    if not os.path.isfile(cli):
        npm = need('npm', 'Install Node.js 18 or newer from https://nodejs.org/.')
        subprocess.run([npm, 'install', '--prefix', TOOLS, '--no-save', '--no-audit', '--no-fund',
                        f'gltfpack@{GLTFPACK_VERSION}'], check=True, stdout=subprocess.DEVNULL)
    return [node, cli]


def pack_all(jobs):
    """jobs: list of (src, dst, extra args). Runs gltfpack on each, six at a time."""
    gltfpack = gltfpack_command()

    def pack(job):
        src, dst, extra = job
        r = subprocess.run([*gltfpack, '-i', src, '-o', dst, *GLTFPACK_COMMON, *extra],
                           capture_output=True, text=True, encoding='utf-8', errors='replace')
        return src if r.returncode != 0 else None

    with ThreadPoolExecutor(6) as ex:
        failed = [f for f in ex.map(pack, jobs) if f]
    for f in failed:
        print('  gltfpack failed: ' + f)
    print(f'  packed {len(jobs) - len(failed)} of {len(jobs)} models', flush=True)


def fresh_dir(path):
    shutil.rmtree(path, ignore_errors=True)
    os.makedirs(path)


def compress(site):
    data = os.path.join(site, 'data')
    jobs = []

    fresh_dir(os.path.join(data, 'tex'))
    for name in os.listdir(os.path.join(OUT, 'tex')):
        shutil.copy2(os.path.join(OUT, 'tex', name), os.path.join(data, 'tex', name))

    # Enemy models are simplified; one node per placement (-kn) for skeletons, wildlife and NPCs
    # so the map can cull each one; level geometry is merged (-mi).
    fresh_dir(os.path.join(data, 'enemies'))
    index = json.load(open(os.path.join(OUT, 'enemies', 'index.json'), encoding='utf-8'))
    for info in index.values():
        ratio = min(1.0, ENEMY_TRIANGLES / max(1, info['tris']))
        jobs.append((os.path.join(OUT, 'enemies', info['glb']), os.path.join(data, 'enemies', info['glb']),
                     ['-si', f'{ratio:.3f}'] if ratio < 1 else []))
    for kind, extra in (('skel', ['-kn']), ('wild', ['-kn']), ('npc', ['-kn']), ('glb', ['-mi'])):
        fresh_dir(os.path.join(data, kind))
        for name in sorted(os.listdir(os.path.join(OUT, kind))):
            if name.endswith('.glb'):
                jobs.append((os.path.join(OUT, kind, name), os.path.join(data, kind, name), extra))
    pack_all(jobs)


def copy_web(site, game):
    for name in os.listdir(WEB):
        src, dst = os.path.join(WEB, name), os.path.join(site, name)
        if os.path.isdir(src):
            shutil.copytree(src, dst, dirs_exist_ok=True)
        else:
            shutil.copy2(src, dst)
    build = game_build(game)
    if build:
        index = os.path.join(site, 'index.html')
        s = open(index, encoding='utf-8').read()
        s = re.sub(r'\(build [^)]*\)', f'(build {build})', s)
        open(index, 'w', encoding='utf-8', newline='').write(s)
        print(f'  game build {build}')


class QuietHandler(http.server.SimpleHTTPRequestHandler):
    def log_message(self, *args):
        pass  # one line per model and texture request is just noise in the console


def serve(site, port, open_browser=False):
    handler = functools.partial(QuietHandler, directory=site)
    for p in range(port, port + 20):
        try:
            httpd = http.server.ThreadingHTTPServer(('127.0.0.1', p), handler)
            break
        except OSError:
            continue  # port in use
    else:
        fail(f'no free port between {port} and {port + 19}')
    url = f'http://localhost:{httpd.server_address[1]}/'
    print(f'\nMap at {url}\nKeep this window open while you use the map. Close it (or press Ctrl+C) to stop.', flush=True)
    if open_browser:
        import webbrowser
        webbrowser.open(url)
    with httpd:
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            pass


def main():
    ap = argparse.ArgumentParser(description='Build the Queen\'s Domain map from your installed copy of the game.')
    ap.add_argument('--game', help='game install folder (the one with QueensDomain.exe); found through Steam if omitted')
    ap.add_argument('--out', default=os.path.join(ROOT, 'dist'), help='where to write the map (default: ./dist)')
    ap.add_argument('--skip-extract', action='store_true',
                    help='reuse the files already in ./out and only redo compression and web data')
    ap.add_argument('--clean', action='store_true', help='delete the intermediate files in ./out after a successful build')
    ap.add_argument('--serve', action='store_true', help='serve the map on localhost when done')
    ap.add_argument('--serve-only', action='store_true', help='serve an already built map without rebuilding')
    ap.add_argument('--auto', action='store_true',
                    help='serve the existing map if it was built from the installed game version, otherwise rebuild and serve')
    ap.add_argument('--open', action='store_true', help='open the map in the default browser once it is served')
    ap.add_argument('--port', type=int, default=8000)
    args = ap.parse_args()
    site = os.path.abspath(args.out)
    built = os.path.isfile(os.path.join(site, 'data', 'markers.json'))

    if args.serve_only:
        if not built:
            fail(f'no built map in {site}. Run python build.py first.')
        serve(site, args.port, args.open)
        return

    game, paks = resolve_game(args.game)
    current = game_build(game)
    if args.auto and built:
        stamp = os.path.join(site, 'data', BUILD_STAMP)
        made_from = open(stamp, encoding='utf-8').read().strip() if os.path.isfile(stamp) else None
        if made_from == current:
            serve(site, args.port, args.open)
            return
        print(f'The map was built from game build {made_from or "unknown"}; the installed game is build {current}. Rebuilding.')

    print(f'Game: {game}')
    print(f'Map:  {site}')
    if args.auto:
        print('Building the map. This takes 10 to 20 minutes and only happens again after a game update.')
    os.makedirs(site, exist_ok=True)
    t0 = time.time()

    if not args.skip_extract:
        if not os.path.isfile(EXTRACTOR_EXE):
            dotnet = need('dotnet', 'Install the .NET 9 SDK from https://dotnet.microsoft.com/download.')
            header('compile extractor')
            r = subprocess.run([dotnet, 'build', '-c', 'Release', '--nologo', '-v', 'q', os.path.join(ROOT, 'Extractor.csproj')],
                               capture_output=True, text=True, encoding='utf-8', errors='replace')
            if r.returncode != 0:
                print(r.stdout[-4000:], r.stderr[-2000:])
                fail('dotnet build failed')
        env = {**os.environ, 'QD_PAKS': paks}
        os.makedirs(OUT, exist_ok=True)
        header('actors');    extractor(env, 'entities', show='none')
        header('catalog');   extractor(env, 'catalog', show='none')
        header('geometry')
        for d in ('glb', 'tex'):
            shutil.rmtree(os.path.join(OUT, d), ignore_errors=True)
        extractor(env, 'geometry')
        # these run after geometry: they add skeleton, wildlife and NPC textures to out/tex
        for step, d in (('enemies', 'enemies'), ('skeletons', 'skel'), ('wildlife', 'wild'), ('npcs', 'npc')):
            header(step)
            shutil.rmtree(os.path.join(OUT, d), ignore_errors=True)
            extractor(env, step)
    elif not os.path.isfile(os.path.join(OUT, 'entities_raw.json')):
        fail('--skip-extract needs a previous full build in ./out')

    header('compress');  compress(site)
    header('web files'); copy_web(site, game)
    header('web data')
    subprocess.run([sys.executable, os.path.join(ROOT, 'build_data.py'), site], check=True)
    subprocess.run([sys.executable, os.path.join(ROOT, 'stamp_version.py'), site], check=True)
    with open(os.path.join(site, 'data', BUILD_STAMP), 'w', encoding='utf-8') as f:
        f.write(current or '')
    if args.clean:
        shutil.rmtree(OUT, ignore_errors=True)
    print(f'Done in {(time.time() - t0) / 60:.1f} min.')

    if args.serve or args.auto:
        serve(site, args.port, args.open)
    else:
        print(f'To view it: python build.py --serve-only --out "{site}"')


if __name__ == '__main__':
    main()
