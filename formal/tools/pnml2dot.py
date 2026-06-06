#!/usr/bin/env python3
"""
PNML -> Graphviz DOT converter
=============================

Reads a PNML file and emits a DOT graph for visualisation via Graphviz.
Places are circles, transitions are rectangles, arcs are directed edges
with arc-weight labels.

Usage:
  python pnml2dot.py path/to/model.pnml > model.dot
  dot -Tsvg model.dot > model.svg

Then embed model.svg in the README.md.
"""

import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def strip_ns(tag):
    return tag.split("}", 1)[-1] if "}" in tag else tag


def parse_pnml(path):
    tree = ET.parse(path)
    root = tree.getroot()

    places = {}
    transitions = []
    arcs = []

    for el in root.iter():
        tag = strip_ns(el.tag)
        if tag == "place":
            pid = el.get("id")
            im = 0
            for child in el.iter():
                if strip_ns(child.tag) == "initialMarking":
                    for txt in child.iter():
                        if strip_ns(txt.tag) == "text" and txt.text:
                            try:
                                im = int(txt.text.strip())
                            except ValueError:
                                pass
                            break
                    break
            places[pid] = im
        elif tag == "transition":
            transitions.append(el.get("id"))
        elif tag == "arc":
            src = el.get("source")
            tgt = el.get("target")
            weight = 1
            arc_type = el.get("type", "")
            for child in el.iter():
                if strip_ns(child.tag) == "inscription":
                    for txt in child.iter():
                        if strip_ns(txt.tag) == "text" and txt.text:
                            try:
                                weight = int(txt.text.strip())
                            except ValueError:
                                pass
                            break
                    break
            arcs.append((src, tgt, weight, arc_type))

    return places, transitions, arcs


def emit_dot(places, transitions, arcs, name):
    print(f"digraph {name.replace('-', '_')} {{")
    print("  rankdir=LR;")
    print('  node [fontname="Helvetica"];')
    print()

    # Places — circles
    for p, im in places.items():
        label = p.replace("P_", "")
        if im:
            label += f"\\n● x{im}"
        print(f'  "{p}" [shape=circle, label="{label}", style=filled, fillcolor=lightyellow];')
    print()

    # Transitions — rectangles
    for t in transitions:
        label = t.replace("T_", "")
        print(f'  "{t}" [shape=box, label="{label}", style=filled, fillcolor=lightblue];')
    print()

    # Arcs
    for src, tgt, w, atype in arcs:
        wlabel = f' [label="{w}"]' if w != 1 else ""
        if atype == "inhibitor":
            print(f'  "{src}" -> "{tgt}"{wlabel}, arrowhead=odot, style=dashed;')
        else:
            print(f'  "{src}" -> "{tgt}"{wlabel};')

    print("}")


def main():
    if len(sys.argv) < 2:
        print("Usage: python pnml2dot.py path/to/model.pnml", file=sys.stderr)
        sys.exit(1)
    path = Path(sys.argv[1])
    places, transitions, arcs = parse_pnml(path)
    emit_dot(places, transitions, arcs, path.stem)


if __name__ == "__main__":
    main()
