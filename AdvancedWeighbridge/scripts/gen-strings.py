#!/usr/bin/env python3
"""Generates the desktop string dictionaries (en/si/ta .xaml) from Resources/Strings/strings.tsv.

Usage:  python3 scripts/gen-strings.py
The TSV has the columns key, en, si, ta. An empty translation falls back to English.
"""
import csv
import html
import pathlib

folder = pathlib.Path(__file__).resolve().parent.parent / "src" / "Weighbridge.Desktop" / "Resources" / "Strings"
rows = list(csv.DictReader(open(folder / "strings.tsv", encoding="utf-8"), delimiter="\t"))
keys = [r["key"] for r in rows]
duplicates = {k for k in keys if keys.count(k) > 1}
if duplicates:
    raise SystemExit(f"duplicate keys: {sorted(duplicates)}")

for lang in ("en", "si", "ta"):
    out = ['<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
           '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"',
           '                    xmlns:sys="clr-namespace:System;assembly=System.Runtime">']
    if lang == "en":
        out.append("    <!-- Generated from strings.tsv. Edit the TSV and regenerate (scripts/gen-strings.py). -->")
    else:
        out.append("    <!-- Machine-assisted translation: to be reviewed by a native speaker before production use. Missing keys fall back to English. -->")
    for r in rows:
        value = (r[lang] or "").strip() or r["en"]
        out.append(f'    <sys:String x:Key="s.{r["key"]}">{html.escape(value, quote=False)}</sys:String>')
    out.append("</ResourceDictionary>")
    (folder / f"{lang}.xaml").write_text("\n".join(out) + "\n", encoding="utf-8")
print(f"{len(rows)} keys written")
