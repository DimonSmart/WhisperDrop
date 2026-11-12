"""Score the synthetic reference fixture only. Does not print transcript content."""
import argparse
import json
import pathlib
import re

parser = argparse.ArgumentParser()
parser.add_argument("result", type=pathlib.Path, help="Private harness result for synthetic mixed fixture")
args = parser.parse_args()
reference = " ".join(json.loads(pathlib.Path(__file__).with_name("reference_texts.json").read_text()).values())
hypothesis = json.loads(args.result.read_text())["Text"]


def normalize(text):
    return " ".join(re.findall(r"\w+", text.lower()))


def distance(a, b):
    previous = list(range(len(b) + 1))
    for i, x in enumerate(a, 1):
        row = [i]
        for j, y in enumerate(b, 1):
            row.append(min(row[-1] + 1, previous[j] + 1, previous[j - 1] + (x != y)))
        previous = row
    return previous[-1]


r, h = normalize(reference), normalize(hypothesis)
print(json.dumps({"referenceWords": len(r.split()), "hypothesisWords": len(h.split()),
                  "wordEdits": distance(r.split(), h.split()), "wer": distance(r.split(), h.split()) / len(r.split()),
                  "characterEdits": distance(r, h), "cer": distance(r, h) / len(r),
                  "normalization": "Unicode word characters; lower case; punctuation removed; digits not expanded"}, indent=2))
