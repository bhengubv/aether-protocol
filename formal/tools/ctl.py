#!/usr/bin/env python3
"""
Lightweight CTL evaluator for AetherNet formal models.
========================================================

Parses the subset of CTL used in the .q files:

  Boolean atoms over place markings:
    <place> <op> <int>            (atomic comparison)
    <place> + <place> + ... <op> <int>   (sum comparison)

  Operators:
    AG p     — invariant: p holds in every reachable state
    EF p     — reachability: some reachable state satisfies p
    EX p     — exists next-state satisfying p
    AX p     — every next-state satisfies p
    p ⟹ q    — implication
    p ∧ q    — conjunction
    p ∨ q    — disjunction
    ¬ p      — negation

  Nesting allowed: AG (p ⟹ EF q), EF (p ∧ AG q), etc.

Designed to handle the queries we wrote in the .q files for the 20
existing models. Run on the reachability graph produced by verify.py.

Usage:
  from ctl import parse_ctl, evaluate
  ast = parse_ctl("AG (P_Pool >= 1)")
  result = evaluate(ast, reach_graph, places)
"""

import re
from collections import deque


# ── Tokenizer ─────────────────────────────────────────────────────────────────

# Token categories
TOK_LPAREN  = "LPAREN"
TOK_RPAREN  = "RPAREN"
TOK_AND     = "AND"
TOK_OR      = "OR"
TOK_NOT     = "NOT"
TOK_IMPL    = "IMPL"
TOK_AG      = "AG"
TOK_EF      = "EF"
TOK_EX      = "EX"
TOK_AX      = "AX"
TOK_OP      = "OP"          # <, <=, =, >=, >
TOK_PLUS    = "PLUS"
TOK_IDENT   = "IDENT"
TOK_INT     = "INT"
TOK_EOF     = "EOF"


def tokenize(text):
    """Lex CTL formula text into tokens."""
    tokens = []
    # Strip comments
    text = re.sub(r"/\*.*?\*/", " ", text, flags=re.DOTALL)
    text = re.sub(r"//.*$", " ", text, flags=re.MULTILINE)
    # Normalise unicode operators to ASCII
    text = text.replace("⟹", "=>").replace("⟶", "=>").replace("→", "=>")
    text = text.replace("∧", "AND").replace("∨", "OR").replace("¬", "NOT")
    text = text.replace("≤", "<=").replace("≥", ">=").replace("≠", "!=")

    i = 0
    while i < len(text):
        c = text[i]
        if c.isspace():
            i += 1
            continue
        if c == "(":
            tokens.append((TOK_LPAREN, "("))
            i += 1
            continue
        if c == ")":
            tokens.append((TOK_RPAREN, ")"))
            i += 1
            continue
        if c == "+":
            tokens.append((TOK_PLUS, "+"))
            i += 1
            continue
        # Operators: <=, >=, ==, =, <, >, =>
        if text[i:i+2] in ("=>",):
            tokens.append((TOK_IMPL, "=>"))
            i += 2
            continue
        if text[i:i+2] in ("<=", ">=", "=="):
            tokens.append((TOK_OP, text[i:i+2]))
            i += 2
            continue
        if c in "<>=":
            tokens.append((TOK_OP, c))
            i += 1
            continue
        # Identifiers / keywords
        m = re.match(r"[A-Za-z_][A-Za-z0-9_]*", text[i:])
        if m:
            word = m.group()
            upper = word.upper()
            if upper == "AND":
                tokens.append((TOK_AND, word))
            elif upper == "OR":
                tokens.append((TOK_OR, word))
            elif upper == "NOT":
                tokens.append((TOK_NOT, word))
            elif upper == "AG":
                tokens.append((TOK_AG, word))
            elif upper == "EF":
                tokens.append((TOK_EF, word))
            elif upper == "EX":
                tokens.append((TOK_EX, word))
            elif upper == "AX":
                tokens.append((TOK_AX, word))
            else:
                tokens.append((TOK_IDENT, word))
            i += len(word)
            continue
        # Integer literal
        m = re.match(r"\d+", text[i:])
        if m:
            tokens.append((TOK_INT, int(m.group())))
            i += len(m.group())
            continue
        # Unknown char — skip
        i += 1

    tokens.append((TOK_EOF, None))
    return tokens


# ── AST node types ────────────────────────────────────────────────────────────

class Node:
    pass


class Atom(Node):
    """linear_expr op constant  (e.g. P_Pool + P_Spare >= 1)"""
    def __init__(self, places_with_coeffs, op, value):
        self.places = places_with_coeffs    # list of (place_name, coeff)
        self.op = op                        # one of "<", "<=", "=", ">=", ">", "==", "!="
        self.value = value

    def __repr__(self):
        terms = " + ".join(f"{c}*{p}" if c != 1 else p for p, c in self.places)
        return f"({terms} {self.op} {self.value})"


class Not(Node):
    def __init__(self, child):
        self.child = child
    def __repr__(self):
        return f"(NOT {self.child})"


class And(Node):
    def __init__(self, left, right):
        self.left, self.right = left, right
    def __repr__(self):
        return f"({self.left} AND {self.right})"


class Or(Node):
    def __init__(self, left, right):
        self.left, self.right = left, right
    def __repr__(self):
        return f"({self.left} OR {self.right})"


class Impl(Node):
    def __init__(self, left, right):
        self.left, self.right = left, right
    def __repr__(self):
        return f"({self.left} => {self.right})"


class AG(Node):
    def __init__(self, child): self.child = child
    def __repr__(self): return f"(AG {self.child})"


class EF(Node):
    def __init__(self, child): self.child = child
    def __repr__(self): return f"(EF {self.child})"


class EX(Node):
    def __init__(self, child): self.child = child
    def __repr__(self): return f"(EX {self.child})"


class AX(Node):
    def __init__(self, child): self.child = child
    def __repr__(self): return f"(AX {self.child})"


# ── Parser (recursive descent) ────────────────────────────────────────────────

class Parser:
    def __init__(self, tokens):
        self.tokens = tokens
        self.pos = 0

    def peek(self):
        return self.tokens[self.pos]

    def eat(self, kind=None):
        tok = self.tokens[self.pos]
        if kind is not None and tok[0] != kind:
            raise SyntaxError(f"expected {kind}, got {tok}")
        self.pos += 1
        return tok

    def parse(self):
        return self.parse_impl()

    def parse_impl(self):
        left = self.parse_or()
        if self.peek()[0] == TOK_IMPL:
            self.eat(TOK_IMPL)
            right = self.parse_impl()
            return Impl(left, right)
        return left

    def parse_or(self):
        left = self.parse_and()
        while self.peek()[0] == TOK_OR:
            self.eat(TOK_OR)
            right = self.parse_and()
            left = Or(left, right)
        return left

    def parse_and(self):
        left = self.parse_unary()
        while self.peek()[0] == TOK_AND:
            self.eat(TOK_AND)
            right = self.parse_unary()
            left = And(left, right)
        return left

    def parse_unary(self):
        tok = self.peek()
        if tok[0] == TOK_NOT:
            self.eat(TOK_NOT)
            return Not(self.parse_unary())
        if tok[0] in (TOK_AG, TOK_EF, TOK_EX, TOK_AX):
            self.eat(tok[0])
            child = self.parse_unary()
            return {TOK_AG: AG, TOK_EF: EF, TOK_EX: EX, TOK_AX: AX}[tok[0]](child)
        if tok[0] == TOK_LPAREN:
            self.eat(TOK_LPAREN)
            inner = self.parse_impl()
            self.eat(TOK_RPAREN)
            return inner
        return self.parse_atom()

    def parse_atom(self):
        """Parse: linear_expr op int  OR  true/false literal"""
        # Boolean literals
        tok = self.peek()
        if tok[0] == TOK_IDENT and tok[1].lower() in ("true", "false"):
            self.eat(TOK_IDENT)
            return Atom([], "=", 1 if tok[1].lower() == "true" else 0)
        places = self.parse_linear()
        op_tok = self.eat(TOK_OP)
        val_tok = self.peek()
        if val_tok[0] == TOK_INT:
            self.eat(TOK_INT)
            value = val_tok[1]
        elif val_tok[0] == TOK_IDENT:
            # Comparison places like "P_S_Count = P_W1_Vouched + ..."
            # treat right side as another linear expr; we'll compare equality
            self.eat(TOK_IDENT)
            right_places = [(val_tok[1], 1)]
            while self.peek()[0] == TOK_PLUS:
                self.eat(TOK_PLUS)
                next_tok = self.eat(TOK_IDENT)
                right_places.append((next_tok[1], 1))
            # Encode "left = right" as Atom(left - right, op, 0)
            combined = list(places)
            for p, c in right_places:
                combined.append((p, -c))
            return Atom(combined, op_tok[1], 0)
        else:
            raise SyntaxError(f"expected int or place after op, got {val_tok}")
        return Atom(places, op_tok[1], value)

    def parse_linear(self):
        """Parse: place [+ place ...]"""
        places = []
        tok = self.eat(TOK_IDENT)
        places.append((tok[1], 1))
        while self.peek()[0] == TOK_PLUS:
            self.eat(TOK_PLUS)
            tok = self.eat(TOK_IDENT)
            places.append((tok[1], 1))
        return places


def parse_ctl(text):
    tokens = tokenize(text)
    return Parser(tokens).parse()


# ── Evaluator ─────────────────────────────────────────────────────────────────

def eval_atom(atom, marking):
    """Evaluate atom on a single marking dict."""
    if not atom.places:
        # boolean literal: empty places list, .value is 1 (true) or 0 (false)
        return atom.value == 1
    total = sum(coeff * marking.get(p, 0) for p, coeff in atom.places)
    return _cmp(total, atom.op, atom.value)


def _cmp(left, op, right):
    if op in ("=", "=="):
        return left == right
    if op == "!=":
        return left != right
    if op == "<":
        return left < right
    if op == "<=":
        return left <= right
    if op == ">":
        return left > right
    if op == ">=":
        return left >= right
    raise ValueError(f"unknown op: {op}")


def eval_propositional(node, marking):
    """Evaluate a propositional sub-formula on a single marking."""
    if isinstance(node, Atom):
        return eval_atom(node, marking)
    if isinstance(node, Not):
        return not eval_propositional(node.child, marking)
    if isinstance(node, And):
        return eval_propositional(node.left, marking) and eval_propositional(node.right, marking)
    if isinstance(node, Or):
        return eval_propositional(node.left, marking) or eval_propositional(node.right, marking)
    if isinstance(node, Impl):
        return (not eval_propositional(node.left, marking)) or eval_propositional(node.right, marking)
    # Temporal — needs the reachability graph; caller handles
    return None


def evaluate(node, markings, successors):
    """
    Evaluate a CTL formula over a finite reachability graph.

    markings: dict mapping state_id -> marking dict
    successors: dict mapping state_id -> set of successor state_ids
    Returns: set of state_ids where the formula holds

    The formula is satisfied "on the graph" if it holds at the initial state
    (the first key of markings).
    """
    if isinstance(node, (Atom, Not, And, Or, Impl)):
        # Propositional — evaluate per-state
        return {sid for sid, m in markings.items()
                if _eval_prop_or_temporal(node, sid, markings, successors)}

    if isinstance(node, AG):
        return _ag(node.child, markings, successors)
    if isinstance(node, EF):
        return _ef(node.child, markings, successors)
    if isinstance(node, EX):
        return _ex(node.child, markings, successors)
    if isinstance(node, AX):
        return _ax(node.child, markings, successors)
    raise ValueError(f"unknown node type: {type(node)}")


def _eval_prop_or_temporal(node, sid, markings, successors):
    """Recursive helper that handles propositional AND temporal nested operators."""
    if isinstance(node, (Atom,)):
        return eval_atom(node, markings[sid])
    if isinstance(node, Not):
        return not _eval_prop_or_temporal(node.child, sid, markings, successors)
    if isinstance(node, And):
        return (_eval_prop_or_temporal(node.left, sid, markings, successors)
                and _eval_prop_or_temporal(node.right, sid, markings, successors))
    if isinstance(node, Or):
        return (_eval_prop_or_temporal(node.left, sid, markings, successors)
                or _eval_prop_or_temporal(node.right, sid, markings, successors))
    if isinstance(node, Impl):
        return ((not _eval_prop_or_temporal(node.left, sid, markings, successors))
                or _eval_prop_or_temporal(node.right, sid, markings, successors))
    # Temporal sub-formula: compute the satisfying set, check membership
    sat = evaluate(node, markings, successors)
    return sid in sat


def _ef(child, markings, successors):
    """EF p: states from which some reachable state satisfies p."""
    # Compute set of states satisfying child (handles nesting)
    sat_child = {sid for sid in markings
                 if _eval_prop_or_temporal(child, sid, markings, successors)}
    # Backward search: any state with a path to sat_child
    result = set(sat_child)
    predecessors = _predecessors(successors)
    queue = deque(sat_child)
    while queue:
        s = queue.popleft()
        for p in predecessors.get(s, ()):
            if p not in result:
                result.add(p)
                queue.append(p)
    return result


def _ag(child, markings, successors):
    """AG p: states from which all reachable states satisfy p.
       Dual: AG p = NOT EF NOT p"""
    sat_child = {sid for sid in markings
                 if _eval_prop_or_temporal(child, sid, markings, successors)}
    # Find states that can reach NOT-sat-child
    bad = set(markings) - sat_child
    can_reach_bad = set(bad)
    predecessors = _predecessors(successors)
    queue = deque(bad)
    while queue:
        s = queue.popleft()
        for p in predecessors.get(s, ()):
            if p not in can_reach_bad:
                can_reach_bad.add(p)
                queue.append(p)
    return set(markings) - can_reach_bad


def _ex(child, markings, successors):
    """EX p: states with at least one successor satisfying p."""
    sat_child = {sid for sid in markings
                 if _eval_prop_or_temporal(child, sid, markings, successors)}
    return {sid for sid in markings if any(s in sat_child for s in successors.get(sid, ()))}


def _ax(child, markings, successors):
    """AX p: states whose every successor satisfies p (vacuously true if no successors)."""
    sat_child = {sid for sid in markings
                 if _eval_prop_or_temporal(child, sid, markings, successors)}
    return {sid for sid in markings if all(s in sat_child for s in successors.get(sid, ()))}


def _predecessors(successors):
    """Invert the successor map."""
    preds = {}
    for s, succs in successors.items():
        for t in succs:
            preds.setdefault(t, set()).add(s)
    return preds


# ── Convenience: load and verify a .q file against a reachability graph ──────

def verify_q_file(q_path, markings, successors, initial_id):
    """Parse a .q file (one query per non-comment line), return list of
       (query_text, satisfied_bool)."""
    results = []
    queries = []
    with open(q_path, "r", encoding="utf-8") as f:
        # Concatenate all lines, strip C-style comments, then split on
        # ALL caps starting a CTL formula.
        text = f.read()
        text = re.sub(r"/\*.*?\*/", "", text, flags=re.DOTALL)
        # Split by newline-EOL after a closing-paren or atomic comparison
        lines = []
        cur = []
        for line in text.splitlines():
            s = line.strip()
            if not s:
                if cur:
                    lines.append(" ".join(cur))
                    cur = []
                continue
            cur.append(s)
        if cur:
            lines.append(" ".join(cur))

        for q in lines:
            q = q.strip()
            if not q or q.startswith("//"):
                continue
            queries.append(q)

    for q in queries:
        try:
            ast = parse_ctl(q)
            sat_set = evaluate(ast, markings, successors)
            satisfied = initial_id in sat_set
            results.append((q, satisfied, None))
        except Exception as e:
            results.append((q, None, str(e)))
    return results


if __name__ == "__main__":
    # Self-test
    text = "AG (P_Pool >= 1)"
    ast = parse_ctl(text)
    print(f"parsed: {ast}")
