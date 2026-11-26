import sys, re, collections
sym = {}
import os
for l in open(os.path.join(os.environ.get('OUT', 'out/switch'), 'hitch-sym.txt')):
    a, f, *w = l.rstrip('\n').split('\t')
    sym[a] = re.sub(r'^.*?(Waddamburo_\w+_Waddamburo_|S_P_CoreLib_)', '', f)[:90] + (' ' + w[0] if w and 'shim' in w[0] else '')
def show(head, t):
    print(head)
    for kind, rows in ('incl', 50), ('leaf', 18):
        print(' ', kind)
        for f, n in sorted(t[kind].items(), key=lambda x: -x[1])[:rows]: print(f'   {n:6} {f}')
t = None
for l in open(sys.argv[1], errors='replace').read().split('\n')[int(sys.argv[2]) - 1:int(sys.argv[3])]:
    if m := re.match(r'\[hitch\] (\d+ frames.*)', l):
        if t: show(head, t)
        head, t = '== ' + m[1], {'incl': collections.Counter(), 'leaf': collections.Counter()}
    elif t and (m := re.match(r'\[hitch\] (incl|leaf):(.*)', l)):
        for n, a in re.findall(r'(\d+)@([0-9a-f]+)', m[2]): t[m[1]][sym.get(a, a)] += int(n)
if t: show(head, t)
