"""WCAG contrast of the chat pane's colour tokens (DESIGN.md, wwwroot/chat.css). Keep the two sets below equal to the
token block in chat.css, then: python tools/chat-contrast.py  -> lists every pair under its bar; exit 1 if any."""
import sys


def lum(h):
    h = h.lstrip('#')
    r, g, b = (int(h[i:i + 2], 16) / 255 for i in (0, 2, 4))
    f = lambda c: c / 12.92 if c <= 0.03928 else ((c + 0.055) / 1.055) ** 2.4
    return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b)


def ratio(a, b):
    la, lb = sorted((lum(a), lum(b)), reverse=True)
    return (la + 0.05) / (lb + 0.05)


SETS = {
    'dark': dict(bg='#1c1e22', raised='#272a30', over='#33373e', fg='#dcdfe4', fg2='#adb2bb', fg3='#969ca6',
                     line='#353941', edge='#7a808a', accent='#7fadf8', onaccent='#10141b', red='#f08a8c', amber='#e2b55a', green='#72c784'),
    'light': dict(bg='#fbfbfc', raised='#f0f1f4', over='#ffffff', fg='#1f2328', fg2='#4a505a', fg3='#666c77',
                  line='#dcdfe4', edge='#7f8590', accent='#1f5fd1', onaccent='#ffffff', red='#b3261e', amber='#8a5a00', green='#1f7a35'),
}
# (foreground, background, least ratio, what it is)
PAIRS = [
    ('fg', 'bg', 4.5, 'body text'), ('fg', 'raised', 4.5, 'text in a bubble, card, composer'), ('fg', 'over', 4.5, 'menu text'),
    ('fg2', 'bg', 4.5, 'secondary text'), ('fg2', 'raised', 4.5, 'secondary on raised'), ('fg2', 'over', 4.5, 'menu description'),
    ('fg3', 'bg', 4.5, 'muted text'), ('fg3', 'raised', 4.5, 'placeholder, muted on raised'), ('amber', 'over', 4.5, 'open elsewhere, in the menu'),
    ('edge', 'bg', 3.0, 'control border on page'), ('edge', 'raised', 3.0, 'control border on raised'),
    ('accent', 'bg', 4.5, 'link'), ('accent', 'raised', 4.5, 'link on raised'), ('onaccent', 'accent', 4.5, 'primary button label'),
    ('red', 'bg', 4.5, 'error text'), ('red', 'raised', 4.5, 'error on raised'),
    ('amber', 'bg', 4.5, 'attention text'), ('amber', 'raised', 4.5, 'attention on raised'), 
    ('green', 'bg', 4.5, 'ok text'),
]
bad = 0
for name, t in SETS.items():
    if len(sys.argv) > 1 and name not in sys.argv[1:]:
        continue
    print(f'== {name}  (bubble vs page {ratio(t["raised"], t["bg"]):.2f}, body {ratio(t["fg"], t["bg"]):.1f})')
    for fg, bg, least, what in PAIRS:
        r = ratio(t[fg], t[bg])
        if r < least:
            bad += 1
            print(f'   FAIL {r:5.2f} < {least}  {fg} {t[fg]} on {bg} {t[bg]}  ({what})')
print('failures:', bad)
sys.exit(1 if bad else 0)
