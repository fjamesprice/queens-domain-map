"""Turn the extractor's raw dumps into the web map's data files.

Inputs (in out/): entities_raw.json, class_defaults.json, catalog_raw.json, icons/, packed/*.glb
Output: <site>/data/{levels.json, markers.json, catalog.json}, <site>/data/icons/, <site>/data/glb/
"""
import json, os, re, shutil, sys, collections

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'out')
if len(sys.argv) < 2:
    sys.exit('usage: python build_data.py <site-dir>')
SITE = sys.argv[1]
DATA = os.path.join(SITE, 'data')

ents = json.load(open(os.path.join(OUT, 'entities_raw.json'), encoding='utf-8'))
cdefs = json.load(open(os.path.join(OUT, 'class_defaults.json'), encoding='utf-8'))
cat = json.load(open(os.path.join(OUT, 'catalog_raw.json'), encoding='utf-8'))
# Posed skeleton models (from `Extractor skeletons`): which placements are real skeletons, and which levels have a model file.
_skel_index = os.path.join(OUT, 'skel', 'index.json')
skel_index = json.load(open(_skel_index, encoding='utf-8')) if os.path.exists(_skel_index) else None
skel_placed = {(lv, actor): pose for lv, actor, pose in skel_index['placed']} if skel_index else None
# Posed wildlife models (from `Extractor wildlife`): which levels have a model file.
_wild_index = os.path.join(OUT, 'wild', 'index.json')
wild_index = json.load(open(_wild_index, encoding='utf-8')) if os.path.exists(_wild_index) else None
# Posed NPC models (from `Extractor npcs`).
_npc_index = os.path.join(OUT, 'npc', 'index.json')
npc_index = json.load(open(_npc_index, encoding='utf-8')) if os.path.exists(_npc_index) else None

ENUMS = {
    'ItemCategory': ['Consumables', 'Accessories', 'Weapons', 'Daggers', 'GemsAndBatteries', 'Keys', 'Food', 'Treasure', 'All'],
    'EnemyResistance': ['More damage', 'Normal damage', 'Less damage', 'Immune'],
    'RuneKeyEnum': ['Puwema', 'Wodaso', 'Sipeha', 'Jigeki', 'Any', 'None'],
}

def enum_val(v, enum):
    if not isinstance(v, str):
        return None
    m = re.search(r'NewEnumerator(\d+)', v)
    if m:
        i = int(m.group(1))
        names = ENUMS.get(enum)
        if names and i < len(names):
            return names[i]
    return v.split('::')[-1]

def norm(p):
    if not p or not isinstance(p, str):
        return None
    p = p.replace('/Game/', 'QueensDomain/Content/')
    if p.endswith('_C') and '.' in p:  # class path -> asset path guess
        pass
    return p

def asset(p):
    return cat.get(norm(p) or '')

def gl(pos):
    # UE (X, Y, Z) cm -> three.js (x, y, z) = (X, Z, Y)
    return [round(pos[0], 1), round(pos[2], 1), round(pos[1], 1)]

NICE = {
    'AquaGate_00_BP_C': 'Temple gate', 'AquaGate_01_BP_C': 'Temple gate', 'SG_Door_Beepee_C': 'Garden door', 'LibGate_00_BP_C': 'Library gate',
    'LibGate_00_BP_Circle_C': 'Library gate', 'EggDoor_BP_C': 'Egg door', 'Castle_Door_Int_BP_C': 'Castle door', 'SZ_JailDoor_BP_C': 'Cell door',
    'ROS_Door_BP_C': 'Record of Sin door', 'Queen_Door_Bp_C': "Queen's door", 'Lib_BossDoor_BP_C': 'Archives door', 'Lib_Door_01_BP_C': 'Library door',
    'M_DoorSmall_00_BP_C': 'Mausoleum door', 'M_WodasGate_00_BP_C': 'Wodaso gate', 'Lib_TreasureGate_BP_C': 'Treasure gate', 'Shop_Door_00_BP_C': 'Shop door',
    'OW_BridgeDoor_C': 'Bridge door', 'OW_TP_Shutter_BP_C': 'Teleporter shutter', 'Ros_Cage_Gate_BP_C': 'Cage gate', 'PT_Door_C': 'Temple door',
    'JosamiPrisonDoor_C': 'Josami prison door', 'JigekiDoor_BP_C': 'Jigeki temple door', 'KitchenDoor_BP_C': 'Kitchen door', 'DesBoss_Gate_C': 'Arena gate',
    'GraveyardBoss_Gate_C': 'Graveyard gate', 'SwordShrineGate_BP_C': 'Sword shrine gate', 'AquaDoor_00_C': 'Sealed temple door', 'DAquaDoor_01_C': 'Dark sealed door',
    'CastleFrontDoor_BP_C': 'Castle front door', 'AquaBossDoor_BP_C': 'Temple boss door', 'AquaBossExitDoor_C': 'Temple boss exit', 'Lib_FrontDoor_BP_C': 'Library front door',
    'Lib_IntDoor_00_BP_C': 'Library door', 'Lib_MidDoor_BP1_C': 'Library door', 'Lib_TrapDoors_00_BP_C': 'Trap doors', 'SZ_Door_02_BP_C': 'Secret zone door',
    'AqrStatuePuzzleDoor_C': 'Statue puzzle door', 'WebBossWall_BP_C': 'Web wall',
    'SecretZoneDoor_Red_BP_C': 'Red secret zone door', 'SecretZoneDoor_Blue_BP_C': 'Blue secret zone door', 'SecretZoneDoor_Green_BP_C': 'Green secret zone door',
    'Forbidden_Door_00_BP_C': 'Forbidden door', 'OfficeDoor_00_BP_C': 'Office door', 'TowerDoor_00_BP_C': 'Tower door', 'BloodRitualDoor_BP_C': 'Blood ritual door',
    'SwordShrineBeams_C': 'Sword shrine', 'BigNut_Interact_C': 'Big Nut', 'StoneGardenPuzzleScript_C': 'Petrified Garden puzzle',
    'ElecPuzzleTerminal_C': 'Electrical puzzle terminal', 'SecretSkullRitual_BP_C': 'Skull ritual', 'SecretTeleporterDestination_C': 'Secret teleporter destination',
    'SuperCubeAltar_C': 'Super Cube altar', 'SpectralSwordAltar_C': 'Spectral Sword altar', 'CurseFountain_BP_C': 'Curse fountain',
    'AQR_KeyRecepticle_BP_C': 'Trident key slot', 'LIB_BookRecepticle_BP_C': 'Book slot', 'MementoRecepticle_C': 'Memento slot',
    'AQ_OfferingPoint_BP_C': 'Offering point', 'SG_Pedestal_TridentKeyReceptacle_C': 'Trident key pedestal',
    'Jigeki_Rune_BP_C': 'Jigeki rune', 'Puwema_Rune_BP_C': 'Puwema rune', 'SipehaRune_BP_C': 'Sipeha rune', 'Wodaso_Rune_BP_C': 'Wodaso rune', 'PTSeal_SipehaRune_BP_C': 'Sipeha seal rune',
    'ScienceSecretWall_C': 'Secret wall (lab)', 'SZ_BoringDoor_00_BP_C': 'Secret wall (secret zone)', 'Gargoyle_Switch_C': 'Gargoyle switch',
    'DaggerButton_DamageTarget_C': 'Dagger target (throw a dagger at it)', 'OW_BridgeLock_BP_C': 'Bridge lock',
}

def pretty(cls):
    if cls in NICE:
        return NICE[cls]
    n = re.sub(r'_C$', '', cls or '')
    n = re.sub(r'(_BP|_Bp|_bp|BP|_NPC|_Pickup|_Item|_Spawner|_Base)+$', '', n)
    n = re.sub(r'_\d+$', '', n)
    n = re.sub(r'_?\d+$', '', n)
    n = re.sub(r'^(OW|AQR|AQ|LIB|Lib|SZ|SG|PT|ROS|Ros|M)_', '', n)
    n = n.replace('_', ' ')
    n = re.sub(r'(?<=[a-z])(?=[A-Z])', ' ', n)
    n = re.sub(r'\b(BP\d*|Bp|bp|00|01)\b', ' ', n)
    n = re.sub(r'\s+', ' ', n).strip()
    return n[:1].upper() + n[1:].lower() if n else n

def eff(a):
    p = dict(cdefs.get(a['class'], {}))
    p.update(a['props'])
    return p

def ref_name(path):
    """'...:PersistentLevel.ActorName' -> ActorName"""
    if not isinstance(path, str):
        return None
    m = re.search(r'PersistentLevel\.([^.:]+)$', path)
    return m.group(1) if m else None

# ---------------------------------------------------------------- levels
LEVEL_INFO = {
    'Ruins_Level_00': ('World (persistent)', 'Shared', False),
    '000_Shared_Base_Lighting': ('Shared lighting', 'Shared', False),
    '050_Ocean': ('Ocean', 'Isle of Neasied', True),
    '060_Ocean_Shadow': ('Ocean shadow', 'Shared', False),
    '070_UpperShell': ('Island terrain (upper shell)', 'Isle of Neasied', True),
    'Intro_Level_00': ('Intro: approach', 'Intro & cutscenes', False),
    'Intro_Level_01': ('Intro: island set', 'Intro & cutscenes', False),
    '100_GR': ('Green Ruins', 'Isle of Neasied', True),
    '110_GR_dungeon': ('Green Ruins dungeon', 'Isle of Neasied', True),
    '120_GR_goldenroom': ('Golden Room', 'Isle of Neasied', True),
    '125_GR_CubeHall': ('Cube Hall', 'Isle of Neasied', True),
    '130_GR_outerisles': ('Outer isles', 'Isle of Neasied', True),
    '140_GR_ShopIsleOnly': ("Umbada's Islet (shop)", 'Isle of Neasied', True),
    # The options menu renders this islet as its backdrop, and the Green Ruins level list streams it in play.
    'VideoConfigMap': ("Umbada's Islet (island)", 'Isle of Neasied', True),
    '200_AQ': ('Obmuer Temple', 'Obmuer Temple', True),
    '210_AQ_underground': ('Obmuer Temple underground', 'Obmuer Temple', True),
    '300_LIB': ('Tower of Knowledge', 'Tower of Knowledge', True),
    # Not a game level: the flesh walls of 300_LIB and 320_LIB_basement, split out by the geometry step (see FLESH below).
    '300_LIB_flesh': ('Flesh walls', 'Tower of Knowledge', True),
    '310_LIB_exterior': ('Tower exterior', 'Tower of Knowledge', True),
    '320_LIB_basement': ('Tower basement', 'Tower of Knowledge', True),
    '400_MAUS_entrance': ('Mausoleum entrance', 'Deimpo Mausoleum', True),
    '420_MAUS_mid': ('Mausoleum middle', 'Deimpo Mausoleum', True),
    '450_MAUS_darkmaze': ('Mausoleum dark maze', 'Deimpo Mausoleum', True),
    '500_AUT': ('Ancient World (Autumn Island)', 'Ancient World', True),
    '510_AUT_droptunnel': ('Drop tunnel', 'Ancient World', True),
    '600_Castle': ('Siraatsu Castle', 'Siraatsu Castle', True),
    '610_Castle_Ext': ('Castle exterior', 'Siraatsu Castle', True),
    '700_SZ_GR': ('Underworld (Halls Shrouded in Darkness)', 'Secret zones', True),
    '710_SZ_AQ': ('Aqua secret zone', 'Secret zones', True),
    '720_SZ_LIB': ('Library secret zone', 'Secret zones', True),
    '730_HOD': ('Hall of the Departed', 'Secret zones', True),
    '740_SZ_Prison': ('Forgotten Dungeon', 'Secret zones', True),
    '750_SZ_MAUS2': ('Mausoleum secret zone', 'Secret zones', True),
    '760_SZ_AUT': ('Record of Sin', 'Secret zones', True),
    '760_Dekanima': ('Dekanima', 'Secret zones', True),
    '770_SZ_Swamp': ('Rotting Cavern', 'Secret zones', True),
    '775_JigekiTemple': ('Jigeki Temple', 'Secret zones', True),
    '780_SZ_Depths': ('Verdant Hollow', 'Secret zones', True),
    '790_SG': ('Petrified Garden', 'Petrified Garden', True),
    # Endgame areas start hidden (no spoilers); they can be switched on in the Areas list.
    '800_FinalZone': ('Final zone', 'Endgame', False),
    '900_SipehaWorld': ("Sipeha's world", 'Endgame', False),
    '990_B33sment': ('B33sment', 'Secret zones', True),
    '997_FinalZoneCutscene': ('Final zone cutscene set', 'Intro & cutscenes', False),
    '998_QueenCutscenes': ('Queen cutscene set', 'Intro & cutscenes', False),
    '999_SipehaCutscenes': ('Sipeha cutscene set', 'Intro & cutscenes', False),
}
SKIP_LEVELS = {'060_Ocean_Shadow', '000_Shared_Base_Lighting', 'Intro_Level_00', 'Intro_Level_01',
               '997_FinalZoneCutscene', '998_QueenCutscenes', '999_SipehaCutscenes'}

# Region names from save points/teleporters located in each level (majority vote).
level_region_votes = collections.defaultdict(collections.Counter)
for lv, L in ents.items():
    for a in L['actors']:
        p = eff(a)
        if a['class'] in ('Checkpoint_C', 'Teleporter_C'):
            ad = asset(p.get('LocationData'))
            if ad and ad['props'].get('Name'):
                level_region_votes[lv][ad['props']['Name']] += 1

CHALLENGES = {}
for v in cat.values():
    if v['type'] == 'FS_ChallengeData':
        for cid, d in v['props'].get('ChallengeDefinitions', []):
            CHALLENGES[cid] = d

# ---------------------------------------------------------------- categories
CATS = [
    # key, label, color, glyph, group, default-on
    ('weapon', 'Weapons', '#ff5c5c', 'W', 'Items', True),
    ('dagger', 'Daggers', '#ff8a5c', 'D', 'Items', True),
    ('accessory', 'Accessories', '#ff6fd8', 'A', 'Items', True),
    ('consumable', 'Consumables', '#7cff7c', 'C', 'Items', False),
    ('key', 'Key items', '#ffd84a', 'K', 'Items', True),
    ('collectible', 'Collectibles (skulls, eggs)', '#fff3b0', 'G', 'Items', True),
    ('treasure', 'Treasure', '#ffb347', 'T', 'Items', True),
    ('upgrade', 'Upgrades (food, gems, cells)', '#c78bff', 'U', 'Items', True),
    ('coin', 'Coins', '#f5e663', '$', 'Items', False),
    ('container', 'Chests & breakables with loot', '#d9a066', 'B', 'Items', True),
    ('enemy', 'Enemies', '#e04040', 'E', 'Enemies', False),
    ('boss', 'Bosses', '#ff2020', 'X', 'Enemies', True),
    ('mimic', 'Mimics', '#ff4fa0', 'M', 'Enemies', True),
    ('npc', 'NPCs', '#5cd6ff', 'N', 'People & places', True),
    ('shop', 'Shops', '#4affc3', '¤', 'People & places', True),
    ('save', 'Save points', '#4a9dff', 'S', 'People & places', True),
    ('teleporter', 'Teleporters', '#a07bff', 'P', 'People & places', True),
    ('lore', 'Notes & inscriptions', '#e8e0c8', '?', 'People & places', False),
    ('secret', 'Secret walls', '#00ffa0', '!', 'Secrets', True),
    ('secretdoor', 'Secret zone & forbidden doors', '#00e0ff', '#', 'Secrets', True),
    ('rune', 'Rune switches', '#b0ff3a', 'R', 'Secrets', True),
    ('special', 'Altars & shrines', '#ffe0a0', '*', 'Secrets', True),
    ('lockeddoor', 'Locked doors', '#ffa040', 'L', 'Doors & mechanisms', True),
    ('receptacle', 'Key slots & pedestals', '#ffcc80', 'O', 'Doors & mechanisms', False),
    ('mechanism', 'Levers & lifts', '#9aa8b8', '=', 'Doors & mechanisms', False),
    ('challenge', 'Challenge triggers', '#ffffff', 'Q', 'Doors & mechanisms', False),
    ('wildlife', 'Wildlife', '#8fbf6f', 'w', 'Ambient', False),
    ('skeleton', 'Skeletons (many point to secrets)', '#d6c9a8', 'b', 'Ambient', False),
]
CAT_KEYS = {c[0] for c in CATS}

ITEM_CAT_MAP = {'Weapons': 'weapon', 'Daggers': 'dagger', 'Accessories': 'accessory', 'Consumables': 'consumable',
                'Keys': 'key', 'Treasure': 'treasure', 'Food': 'upgrade', 'GemsAndBatteries': 'upgrade'}
DIFF = {'EFSPD_Hidden': 'Hidden', 'EFSPD_OutOfView': 'Out of view', 'EFSPD_AccessibleLater': 'Accessible later',
        'EFSPD_MainPath': 'On the main path'}

# ---------------------------------------------------------------- catalog
icons_used = set()
items, enemies = {}, {}

def item_entry(path):
    path = norm(path)
    if not path:
        return None
    a = cat.get(path)
    if not a:
        return None
    key = path.split('.')[-1]
    if key in items:
        return key
    p = a['props']
    category = enum_val(p.get('Category'), 'ItemCategory')
    folder = path.split('/')
    c = ITEM_CAT_MAP.get(category)
    if len(folder) > 3 and folder[3] == 'Upgrades':
        c = 'upgrade'
    if 'Coins' in folder or key.lower().startswith('coin'):
        c = 'coin'
    if not c:
        f = folder[3] if len(folder) > 3 else ''
        c = {'Weapons': 'weapon', 'Accessories': 'accessory', 'Consumables': 'consumable', 'KeyItems': 'key',
             'Treasure': 'treasure', 'Upgrades': 'upgrade', 'Artifacts': 'accessory'}.get(f, 'treasure')
    ent = {'name': p.get('Name') or pretty(key), 'desc': p.get('Item Description') or '', 'cat': c}
    if a.get('ItemIcon_png'):
        ent['icon'] = a['ItemIcon_png']
        icons_used.add(a['ItemIcon_png'])
    if p.get('SellValue'):
        ent['sell'] = p['SellValue']
    items[key] = ent
    return key

def enemy_entry(path):
    path = norm(path)
    a = cat.get(path or '')
    if not a:
        return None
    key = path.split('.')[-1]
    if key in enemies:
        return key
    p = a['props']
    res = {}
    for r in ('Slash', 'Blunt', 'Pierce', 'Fire', 'Disease'):
        v = p.get(r + 'Resistance')
        if v:
            res[r] = enum_val(v, 'EnemyResistance')
    drops = []
    d = p.get('Drop') or {}
    for i, ch in (('Item', 'Chance'), ('Item2', 'Chance2')):
        it = item_entry(d.get(i)) if d.get(i) not in (None, 'None') else None
        if it and d.get(ch):
            drops.append({'item': it, 'chance': d.get(ch)})
    e = {'name': p.get('Name') or pretty(key), 'desc': p.get('Description') or '', 'hp': p.get('HP'),
         'dmg': p.get('BaseDamage'), 'xp': p.get('XPReward'), 'res': res, 'drops': drops, 'boss': bool(p.get('Boss'))}
    st = p.get('StatusType')
    if st:
        e['status'] = str(st).split('::')[-1]
    enemies[key] = e
    return key

# ---------------------------------------------------------------- markers
markers = []
levels_out = []
by_level_name = {}  # (level, actorName) -> marker index
pending_refs = []    # (marker idx, kind, [actor names])

def add(lv_idx, a, cat_key, name, **kw):
    m = {'c': cat_key, 'l': lv_idx, 'p': gl(a['pos']), 'n': name, 'a': a['name']}
    for k, v in kw.items():
        if v not in (None, '', [], {}):
            m[k] = v
    markers.append(m)
    return len(markers) - 1

level_names = [lv for lv in ents if lv not in SKIP_LEVELS]
# The flesh walls get their own area row (right after the tower) when the geometry step wrote their file. Their actors
# only give the row its bounds; they make no markers.
FLESH = '300_LIB_flesh'
if os.path.exists(os.path.join(OUT, 'glb', FLESH + '.glb')) and '300_LIB' in level_names:
    level_names.insert(level_names.index('300_LIB') + 1, FLESH)
    ents[FLESH] = {'actors': [a for src in ('300_LIB', '320_LIB_basement') for a in ents.get(src, {}).get('actors', [])
                              if 'Necro_Deco_Base_C' in a['anc']]}
for li, lv in enumerate(level_names):
    L = ents[lv]
    info = LEVEL_INFO.get(lv, (pretty(lv), 'Other', True))
    region = info[1]
    votes = level_region_votes.get(lv)
    pts = []
    for a in L['actors']:
        p = eff(a)
        anc = a['anc']
        cls = a['class']
        idx = None
        if a['pos'] != [0, 0, 0]:
            pts.append(a['pos'])
        if 'CoreItemPickupActor_C' in anc:
            grants = p.get('ItemsToGrant') or []
            keys = []
            for g in grants:
                k = item_entry(g[0]) if g else None
                if k:
                    keys.append([k, g[1] if len(g) > 1 else 1])
            if not keys:
                continue
            first = items[keys[0][0]]
            name = first['name'] + (f" ×{keys[0][1]}" if keys[0][1] and keys[0][1] > 1 else '')
            diff = DIFF.get(str(p.get('PickupDifficulty', '')).split('::')[-1])
            idx = add(li, a, first['cat'], name, it=keys, diff=diff, act=bool(p.get('RequireActivation')) or None)
        elif 'EnemySpawner_C' in anc:
            ek = enemy_entry(p.get('EnemyData'))
            if not ek:
                continue
            e = enemies[ek]
            c = 'boss' if e['boss'] else ('mimic' if 'Mimic' in cls else 'enemy')
            once = p.get('MaxKillCount') == 1 and p.get('UseMaxKillCount') is not False
            idx = add(li, a, c, e['name'], en=ek, trap=('Trap' in cls) or None, once=once or None, y=round(a.get('yaw', 0), 1) or None)
        elif 'NPC_Base_C' in anc or cls == 'Shopkeeper_C':
            name = p.get('name') if isinstance(p.get('name'), str) else None
            ghost = 'Ghost' in cls
            name = name or pretty(cls).replace(' Ghost', '')
            if cls == 'SecretSword_Altar_BP_C':
                idx = add(li, a, 'special', 'Secret Sword Altar', cls=cls)
                continue
            shop = p.get('Shop Data')
            kind = 'shop' if shop or cls == 'Shopkeeper_C' else 'npc'
            extra = {}
            if shop:
                sd = asset(shop)
                if sd:
                    stock = []
                    for sid, s in sd['props'].get('Stock', []):
                        k = item_entry(s.get('DisplayItem')) or (item_entry(s['ItemsToGrant'][0][0]) if s.get('ItemsToGrant') else None)
                        if k:
                            stock.append({'item': k, 'price': s.get('Price'), 'inf': s.get('bInfiniteStock') or None,
                                          'stock': s.get('BaseStock')})
                    extra['stock'] = stock
            rewards = []
            for r in p.get('RewardList') or []:
                pick = r.get('ItemPickup') if isinstance(r, dict) else None
                if pick:
                    pick_cls = pick.split('.')[-1]
                    g = (cdefs.get(pick_cls) or {}).get('ItemsToGrant') or []
                    k = item_entry(g[0][0]) if g else None
                    rewards.append({'need': r.get('AmountNeeded'), 'item': k or pretty(pick_cls)})
            idx = add(li, a, kind, name + (' (ghost)' if ghost else ''), cls=cls, rewards=rewards, **extra)
        elif cls == 'ShopTreasureActor_C':
            continue
        elif 'SecretWall_C' in anc:
            nm = 'Secret wall' if cls == 'SecretWall_C' else pretty(cls)
            idx = add(li, a, 'secret', nm, cls=cls)
            pending_refs.append((idx, 'triggers', [ref_name(x) for x in (p.get('ActivatableTargets') or [])]))
        elif 'SecretZoneDoor_BP_C' in anc or 'ForbiddenDoor_BaseClass_C' in anc:
            keys = [item_entry(k[0]) for k in (p.get('KeyItems') or []) if k and k[0]]
            idx = add(li, a, 'secretdoor', pretty(cls), keys=[k for k in keys if k], cls=cls)
        elif 'RuneButton_C' in anc or 'SipehaRune_BP_C' in anc:
            rk = enum_val(p.get('RequiredRuneKeyIndex'), 'RuneKeyEnum')
            idx = add(li, a, 'rune', pretty(cls), rune=rk, cls=cls)
            pending_refs.append((idx, 'triggers', [ref_name(x) for x in (p.get('ActivatableTargets') or [])]))
        elif 'Door_Base_C' in anc:
            keys = [item_entry(k[0]) for k in (p.get('KeyItems') or []) if k and k[0]]
            keys = [k for k in keys if k]
            if keys:
                idx = add(li, a, 'lockeddoor', pretty(cls), keys=keys, cls=cls)
            elif p.get('ScriptOpenable') or (p.get('ActivationsNeededForOpen') or 0) > 1:
                idx = add(li, a, 'lockeddoor', pretty(cls) + ' (opened by mechanism)', cls=cls,
                          needs=p.get('ActivationsNeededForOpen'))
        elif 'AQR_KeyRecepticle_BP_C' in anc or cls == 'AQR_KeyRecepticle_BP_C':
            vk = p.get('SpecifiedKeys') if p.get('RequiresSpecifiedKey') else p.get('ValidKeys')
            keys = [item_entry(k if isinstance(k, str) else k[0]) for k in (vk or [])]
            idx = add(li, a, 'receptacle', pretty(cls), keys=[k for k in keys if k][:12], cls=cls)
        elif 'Chest_C' in anc or 'Permabreakable_base_C' in anc or 'Pot_Base_C' in anc or cls in ('BreakableBarricade_C', 'Wellcover_Breakable_C'):
            refs = p.get('ActorsToActivateOnOpen') or p.get('ActorsToActivateOnBreak') or []
            refs = [ref_name(x) for x in refs]
            if not refs and 'Chest_C' not in anc:
                continue
            nm = 'Chest' if 'Chest_C' in anc else pretty(cls)
            if cls in ('Chest_Knights_C', 'Chest_Merchant_C', 'Chest_Raiders_C'):
                nm = pretty(cls).replace('Chest ', '') + "' chest"
            idx = add(li, a, 'container', nm, cls=cls)
            pending_refs.append((idx, 'contains', refs))
        elif cls == 'Checkpoint_C':
            ad = asset(p.get('LocationData'))
            nm, reg, img = 'Save point', None, None
            if ad:
                nm = ad['props'].get('AreaName') or nm
                reg = ad['props'].get('Name')
                img = ad.get('Image_png')
            if img:
                icons_used.add(img)
            idx = add(li, a, 'save', nm, region=reg, img=img)
        elif cls in ('Teleporter_C', 'MiniTeleporter_C'):
            ad = asset(p.get('LocationData') or p.get('LocationDA'))
            nm, desc, img = 'Teleporter', None, None
            if ad:
                nm = (ad['props'].get('Name') or nm) + (' (mini teleporter)' if cls == 'MiniTeleporter_C' else ' teleporter')
                desc = ad['props'].get('Description')
                img = ad.get('Image_png')
            if img:
                icons_used.add(img)
            idx = add(li, a, 'teleporter', nm, desc=desc, img=img)
        elif 'InspectText_C' in anc or cls == 'InspectText_C':
            title = p.get('Title') if isinstance(p.get('Title'), str) else None
            text = p.get('Text') if isinstance(p.get('Text'), str) else None
            if not (title or text):
                continue
            idx = add(li, a, 'lore', title or pretty(cls), text=text)
        elif cls in ('SpectralSwordAltar_C', 'SuperCubeAltar_C', 'CurseFountain_BP_C', 'SecretSkullRitual_BP_C',
                     'SecretTeleporterDestination_C', 'SwordShrineBeams_C', 'Gem_Pedestal_Base_BP_C', 'ElecPuzzleTerminal_C',
                     'StoneGardenPuzzleScript_C', 'BigNut_Interact_C'):
            idx = add(li, a, 'special', pretty(cls), cls=cls)
        elif 'Lever_C' in anc or 'ElevatorLever_C' in anc or 'Elevator_Base_C' in anc or cls in (
                'Gargoyle_Switch_C', 'DaggerButton_DamageTarget_C', 'OW_BridgeLock_BP_C', 'Elevator_Test_C', 'OW_TimedLift_00_BP_C'):
            idx = add(li, a, 'mechanism', pretty(cls), cls=cls)
            pending_refs.append((idx, 'triggers', [ref_name(x) for x in (p.get('ActivatableTargets') or p.get('ActivationTargets') or [])]))
        elif cls == 'ChallengeCompleteVolume_C':
            cid = p.get('ChallengeToComplete')
            ch = CHALLENGES.get(cid, {})
            idx = add(li, a, 'challenge', 'Challenge: ' + (ch.get('ImportedName') or str(cid or '?')),
                      desc=ch.get('ImportedDescription'))
        elif any(x in anc for x in ('Lizard_Base_BP_C', 'Crab_Base_BP_C', 'Bug_Base_BP_C', 'Rat_Base_BP_C', 'FS_CrowBase', 'Crow_Bp_C')):
            idx = add(li, a, 'wildlife', pretty(cls), cls=cls)
        elif cls in ('Boner_TrapDecoy_C', 'Boner_TrapDecoy_RelicHead_C'):
            # Posed skeleton props. Many are placed pointing at secrets. Hidden ones (final zone) are skipped.
            if p.get('bHidden'):
                continue
            # The same prop class also places dead knights and creature statues; the skeleton export knows which are skeletons.
            if skel_placed is not None and (lv, a['name']) not in skel_placed:
                continue
            idx = add(li, a, 'skeleton', 'Skeleton')
        elif cls in ('ActivatableGroup_C', 'ActivationTrigger_C'):
            # not shown, but used to resolve ambushes
            pending_refs.append((None, 'group:' + a['name'], [ref_name(x) for x in (p.get('ActivationTargets') or [])]))
            by_level_name[(li, a['name'])] = ('group', a['name'])
            continue
        if idx is not None:
            by_level_name[(li, a['name'])] = idx
    # level bounds from actor positions (robust percentiles)
    bounds = None
    if pts:
        xs = sorted(q[0] for q in pts); ys = sorted(q[1] for q in pts); zs = sorted(q[2] for q in pts)
        def pct(v, f):
            return v[min(len(v) - 1, max(0, int(len(v) * f)))]
        lo = [pct(xs, .02), pct(ys, .02), pct(zs, .02)]
        hi = [pct(xs, .98), pct(ys, .98), pct(zs, .98)]
        bounds = [gl(lo), gl(hi)]
    vote = votes.most_common(1)[0][0] if votes else None
    levels_out.append({'id': lv, 'name': info[0], 'region': region if region not in ('Secret zones',) or not vote else region,
                       'areaName': vote, 'on': info[2], 'glb': f'glb/{lv}.glb', 'bounds': bounds})
    if skel_index and lv in skel_index['levels']:
        levels_out[-1]['skel'] = f'skel/{lv}.glb'
    if wild_index and lv in wild_index['levels']:
        levels_out[-1]['wild'] = f'wild/{lv}.glb'
    if npc_index and lv in npc_index['levels']:
        levels_out[-1]['npc'] = f'npc/{lv}.glb'

# Key items placed many times are collectibles (etched skulls, eggs...)
key_counts = collections.Counter(m['it'][0][0] for m in markers if m['c'] == 'key' and m.get('it'))
for m in markers:
    if m['c'] == 'key' and m.get('it') and key_counts[m['it'][0][0]] >= 10:
        m['c'] = 'collectible'
        items[m['it'][0][0]]['cat'] = 'collectible'

# ---------------------------------------------------------------- resolve references
group_targets = {}
for idx, kind, names in pending_refs:
    if kind.startswith('group:'):
        group_targets[kind[6:]] = [n for n in names if n]

def level_of(idx):
    return markers[idx]['l']

def resolve(li, names, depth=0):
    out = []
    for n in names:
        if not n:
            continue
        t = by_level_name.get((li, n))
        if isinstance(t, int):
            out.append(t)
        elif isinstance(t, tuple) and depth < 3:
            out.extend(resolve(li, group_targets.get(t[1], []), depth + 1))
    return out

triggered = collections.Counter()
for idx, kind, names in pending_refs:
    if idx is None:
        # trigger volumes/groups: mark enemies they release as ambushes
        for t in resolve(markers[0]['l'] if False else None, [], 0):
            pass
        continue
    li = level_of(idx)
    tgt = resolve(li, names)
    if not tgt:
        continue
    if kind == 'contains':
        markers[idx]['in'] = [t for t in tgt if markers[t]['c'] not in ('enemy', 'boss', 'mimic')]
        amb = [t for t in tgt if markers[t]['c'] in ('enemy', 'boss', 'mimic')]
        if amb:
            markers[idx]['amb'] = amb
        for t in markers[idx]['in']:
            markers[t]['inside'] = idx
        for t in amb:
            markers[t]['ambush'] = True
    else:
        markers[idx]['trig'] = tgt
        for t in tgt:
            if markers[t]['c'] in ('enemy', 'boss', 'mimic'):
                markers[t]['ambush'] = True

# Enemies released by trigger volumes / activatable groups are ambushes.
for li, lv in enumerate(level_names):
    for a in ents[lv]['actors']:
        if a['class'] in ('ActivatableGroup_C', 'ActivationTrigger_C'):
            p = eff(a)
            for t in resolve(li, [ref_name(x) for x in (p.get('ActivationTargets') or [])]):
                if markers[t]['c'] in ('enemy', 'boss', 'mimic'):
                    markers[t]['ambush'] = True

# Containers whose contents were all unresolvable and that are breakables: drop them.
markers_final = []
remap = {}
for i, m in enumerate(markers):
    if m['c'] == 'container' and not m.get('in') and not m.get('amb') and 'Chest' not in m.get('cls', '') and m['n'] != 'Chest':
        continue
    remap[i] = len(markers_final)
    markers_final.append(m)
for m in markers_final:
    for k in ('in', 'amb', 'trig'):
        if k in m:
            m[k] = [remap[t] for t in m[k] if t in remap]
    if 'inside' in m:
        if m['inside'] in remap:
            m['inside'] = remap[m['inside']]
        else:
            del m['inside']
    m.pop('a', None)

# enemy models (from `Extractor enemies`)
emi = os.path.join(OUT, 'enemies', 'index.json')
if os.path.exists(emi):
    models = json.load(open(emi, encoding='utf-8'))
    for k, e in enemies.items():
        if k in models:
            e['model'] = models[k]['glb']

# ---------------------------------------------------------------- write
os.makedirs(os.path.join(DATA, 'icons'), exist_ok=True)
for ic in icons_used:
    src = os.path.join(OUT, 'icons', ic)
    if os.path.exists(src):
        shutil.copy2(src, os.path.join(DATA, 'icons', ic))

counts = collections.Counter(m['c'] for m in markers_final)
cats_out = [{'key': k, 'label': l, 'color': c, 'glyph': g, 'group': grp, 'on': on, 'count': counts.get(k, 0)}
            for k, l, c, g, grp, on in CATS]
json.dump({'levels': levels_out, 'categories': cats_out}, open(os.path.join(DATA, 'levels.json'), 'w', encoding='utf-8'),
          ensure_ascii=False, separators=(',', ':'))
json.dump({'markers': markers_final}, open(os.path.join(DATA, 'markers.json'), 'w', encoding='utf-8'),
          ensure_ascii=False, separators=(',', ':'))
json.dump({'items': items, 'enemies': enemies}, open(os.path.join(DATA, 'catalog.json'), 'w', encoding='utf-8'),
          ensure_ascii=False, separators=(',', ':'))

print(f'{len(levels_out)} levels, {len(markers_final)} markers, {len(items)} items, {len(enemies)} enemies, {len(icons_used)} icons')
for k, l, *_ in CATS:
    print(f'  {k:12} {counts.get(k, 0)}')
