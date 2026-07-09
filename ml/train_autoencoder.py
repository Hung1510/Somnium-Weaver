#!/usr/bin/env python3
"""
train a tiny autoencoder for somnium weaver's anomaly engine and export it to ONNX.

why an autoencoder: the app standardizes each signal online (per-feature EWMA -> ~N(0,1))
before inference, so the model only ever sees standardized data. we train it on standardized
"normal" telemetry, so it learns the SHAPE of normal cross-signal correlations (high load ->
high temp, etc.). at runtime, reconstruction error spikes when that joint pattern breaks --
e.g. temp high while load is normal -- which a per-signal z-score can't catch.

usage:
    python train_autoencoder.py                 # train on synthetic normal data
    python train_autoencoder.py path/to/telemetry.csv   # train on YOUR captured data

capture your own data by enabling "log telemetry csv" in the app (writes
%AppData%/SomniumWeaver/telemetry.csv while you use the machine normally), then retrain.

outputs (next to the repo's SomniumWeaver.csproj):
    model/anomaly_autoencoder.onnx
    model/anomaly_meta.json
"""
import json
import os
import sys
import numpy as np

FEATURES = ["cpu", "ram", "net", "gpu", "cpu-temp", "gpu-temp"]
ALPHA = 0.05          # must match the app's runtime standardization EWMA
BOTTLENECK = 2
HIDDEN = 4
EPOCHS = 4000
LR = 0.02
SEED = 7

rng = np.random.default_rng(SEED)


def synthetic_normal(n=8000):
    """correlated 'normal' telemetry. a shared load factor drives cpu/gpu, and each pushes
    its own temperature; ram loosely tracks load; net is mostly independent."""
    g = rng.normal(0, 1, n)                       # global activity
    cpu = 0.8 * g + rng.normal(0, 0.5, n)
    gpu = 0.7 * g + rng.normal(0, 0.6, n)
    cpu_temp = 0.85 * cpu + rng.normal(0, 0.4, n)
    gpu_temp = 0.85 * gpu + rng.normal(0, 0.4, n)
    ram = 0.4 * g + rng.normal(0, 0.8, n)
    net = 0.2 * g + rng.normal(0, 1.0, n)
    return np.stack([cpu, ram, net, gpu, cpu_temp, gpu_temp], axis=1)


def load_csv(path):
    import csv
    cols = {f: [] for f in FEATURES}
    with open(path, newline="") as fh:
        for row in csv.DictReader(fh):
            for f in FEATURES:
                v = row.get(f, "")
                cols[f].append(float(v) if v not in ("", None) else np.nan)
    data = np.array([cols[f] for f in FEATURES], dtype=float).T
    # drop rows with any missing feature for training simplicity
    data = data[~np.isnan(data).any(axis=1)]
    if len(data) < 200:
        raise SystemExit(f"only {len(data)} complete rows in {path}; capture more first.")
    return data


def standardize(x):
    mu = x.mean(axis=0)
    sd = x.std(axis=0)
    sd[sd < 1e-6] = 1.0
    return (x - mu) / sd


def tanh(z):
    return np.tanh(z)


def train(X):
    n, f = X.shape
    # weights: f -> HIDDEN -> BOTTLENECK -> HIDDEN -> f
    def init(a, b):
        return rng.normal(0, np.sqrt(2.0 / (a + b)), (a, b))

    W1, b1 = init(f, HIDDEN), np.zeros(HIDDEN)
    W2, b2 = init(HIDDEN, BOTTLENECK), np.zeros(BOTTLENECK)
    W3, b3 = init(BOTTLENECK, HIDDEN), np.zeros(HIDDEN)
    W4, b4 = init(HIDDEN, f), np.zeros(f)

    for epoch in range(EPOCHS):
        z1 = X @ W1 + b1; a1 = tanh(z1)
        z2 = a1 @ W2 + b2; a2 = tanh(z2)
        z3 = a2 @ W3 + b3; a3 = tanh(z3)
        out = a3 @ W4 + b4                          # linear output

        diff = out - X
        loss = np.mean(diff ** 2)
        scale = 2.0 / (n * f)
        dout = diff * scale

        dW4 = a3.T @ dout; db4 = dout.sum(0)
        da3 = dout @ W4.T; dz3 = da3 * (1 - a3 ** 2)
        dW3 = a2.T @ dz3; db3 = dz3.sum(0)
        da2 = dz3 @ W3.T; dz2 = da2 * (1 - a2 ** 2)
        dW2 = a1.T @ dz2; db2 = dz2.sum(0)
        da1 = dz2 @ W2.T; dz1 = da1 * (1 - a1 ** 2)
        dW1 = X.T @ dz1; db1 = dz1.sum(0)

        for w, g in ((W1, dW1), (W2, dW2), (W3, dW3), (W4, dW4),
                     (b1, db1), (b2, db2), (b3, db3), (b4, db4)):
            w -= LR * g

        if epoch % 800 == 0:
            print(f"  epoch {epoch:4d}  loss {loss:.5f}")

    return (W1, b1, W2, b2, W3, b3, W4, b4)


def recon_errors(X, params):
    W1, b1, W2, b2, W3, b3, W4, b4 = params
    a1 = tanh(X @ W1 + b1)
    a2 = tanh(a1 @ W2 + b2)
    a3 = tanh(a2 @ W3 + b3)
    out = a3 @ W4 + b4
    return np.mean((out - X) ** 2, axis=1)


def export_onnx(params, path):
    from onnx import TensorProto, helper, numpy_helper, checker

    W1, b1, W2, b2, W3, b3, W4, b4 = params
    inits, nodes = [], []

    def add(name, arr):
        inits.append(numpy_helper.from_array(arr.astype(np.float32), name))

    add("W1", W1); add("b1", b1)
    add("W2", W2); add("b2", b2)
    add("W3", W3); add("b3", b3)
    add("W4", W4); add("b4", b4)

    # Gemm(A, B, C) = A@B + C ; tanh on the three hidden layers, linear output
    nodes.append(helper.make_node("Gemm", ["input", "W1", "b1"], ["g1"]))
    nodes.append(helper.make_node("Tanh", ["g1"], ["h1"]))
    nodes.append(helper.make_node("Gemm", ["h1", "W2", "b2"], ["g2"]))
    nodes.append(helper.make_node("Tanh", ["g2"], ["h2"]))
    nodes.append(helper.make_node("Gemm", ["h2", "W3", "b3"], ["g3"]))
    nodes.append(helper.make_node("Tanh", ["g3"], ["h3"]))
    nodes.append(helper.make_node("Gemm", ["h3", "W4", "b4"], ["output"]))

    f = W1.shape[0]
    inp = helper.make_tensor_value_info("input", TensorProto.FLOAT, [1, f])
    outp = helper.make_tensor_value_info("output", TensorProto.FLOAT, [1, f])
    graph = helper.make_graph(nodes, "somnium_autoencoder", [inp], [outp], inits)
    model = helper.make_model(graph, opset_imports=[helper.make_opsetid("", 13)])
    model.ir_version = 9
    checker.check_model(model)

    import onnx
    onnx.save(model, path)


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    out_dir = os.path.join(os.path.dirname(here), "model")
    os.makedirs(out_dir, exist_ok=True)

    if len(sys.argv) > 1:
        print(f"training on captured data: {sys.argv[1]}")
        raw = load_csv(sys.argv[1])
    else:
        print("training on synthetic normal data (pass a telemetry.csv to use your own)")
        raw = synthetic_normal()

    X = standardize(raw).astype(np.float64)
    split = int(0.85 * len(X))
    Xtr, Xval = X[:split], X[split:]

    print(f"training autoencoder on {len(Xtr)} samples, {X.shape[1]} features")
    params = train(Xtr)

    errs = recon_errors(Xval, params)
    threshold = float(np.percentile(errs, 99))
    print(f"validation recon error: mean={errs.mean():.5f}  p99(threshold)={threshold:.5f}")

    model_path = os.path.join(out_dir, "anomaly_autoencoder.onnx")
    meta_path = os.path.join(out_dir, "anomaly_meta.json")
    export_onnx(params, model_path)
    with open(meta_path, "w") as fh:
        json.dump({
            "features": FEATURES,
            "alpha": ALPHA,
            "threshold": threshold,
            "input_name": "input",
            "note": "reconstruction-error autoencoder over online-standardized signals",
        }, fh, indent=2)

    print(f"wrote {model_path}")
    print(f"wrote {meta_path}")


if __name__ == "__main__":
    main()
