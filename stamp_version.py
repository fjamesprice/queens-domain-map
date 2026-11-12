"""Write a content version into the map's index.html (app.js?v=..., style.css?v=...).

The site sits behind Cloudflare, which caches .js/.css (and images) at its edge, so a plain
reload can keep serving old files after an update. app.js passes the same version on to every
data, model and texture request. Run after changing any site file:  python stamp_version.py <site-dir>
"""
import hashlib, os, re, sys

if len(sys.argv) < 2:
    sys.exit('usage: python stamp_version.py <site-dir>')
SITE = sys.argv[1]

h = hashlib.sha1()
for f in ('app.js', 'style.css', 'data/levels.json', 'data/markers.json', 'data/catalog.json'):
    with open(os.path.join(SITE, f), 'rb') as fh:
        h.update(fh.read())
for d in ('data/glb', 'data/tex', 'data/skel', 'data/wild', 'data/npc'):
    if not os.path.isdir(os.path.join(SITE, d)):
        continue
    for name in sorted(os.listdir(os.path.join(SITE, d))):
        st = os.stat(os.path.join(SITE, d, name))
        h.update(f'{d}/{name}:{st.st_size}:{int(st.st_mtime)}'.encode())
ver = h.hexdigest()[:10]

path = os.path.join(SITE, 'index.html')
s = open(path, encoding='utf-8').read()
s = re.sub(r'href="style\.css(\?v=[^"]*)?"', f'href="style.css?v={ver}"', s)
s = re.sub(r'src="app\.js(\?v=[^"]*)?"', f'src="app.js?v={ver}"', s)
open(path, 'w', encoding='utf-8').write(s)
print('version', ver)
