# usage: dispatch_ids.py [id ...]   -- instance S->C dispatcher: the switch index is this build's case table (see
# client_build.py), indexed by (id - 0x0d), 0x2ed entries. Prints id -> case for the given ids, or every handled id.
# The table address comes from client_build, so a client whose table cannot be located is refused instead of read.
import os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import client_build


def main():
    try:
        name = client_build.build()
        cases = client_build.dispatch_cases()
    except client_build.UnsupportedClient as e:
        print(e, file=sys.stderr)
        return 2
    ids = [int(a, 0) for a in sys.argv[1:]] or sorted(cases)
    print(f"# client {name}: {len(cases)} handled instance ids")
    for i in ids:
        print(f'id {i:#06x} -> ' + (f'case {cases[i]:#x}' if i in cases else 'default (no case)'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
