"""Fits the Jev ticket estimator and writes src/TmTimeTracker/Jev/jev-estimator.json.

The estimator asks Jev one question - how long a ticket takes, as a choice of 15-minute steps -
and turns the answer into minutes with a small linear model on log time, fitted against the
worklog minutes of finished tickets.

Refitting:

    1. Ask Jev the estimator's question about finished tickets, with the key saved in Settings:
         TmTimeTracker.exe --jev-batch tools/jev/questions.json keys.txt answers.jsonl
       keys.txt is one ticket key per line; `python fit_estimator.py --keys keys.txt` writes it.
    2. python tools/jev/fit_estimator.py --answers answers.jsonl
    3. Rebuild. The JSON is embedded in the exe.

Needs Python 3.10+ with numpy and scikit-learn (pip install numpy scikit-learn).

The weights are only valid for the model version that produced the answers, so the model id Jev
reported is written into the JSON and a refit is needed whenever the pinned model changes.
"""
import argparse, csv, json, math, os, sqlite3, sys
from datetime import date
import numpy as np
from sklearn.linear_model import RidgeCV
from sklearn.model_selection import KFold
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import StandardScaler

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
DEFAULT_DB = os.path.join(os.environ.get("LOCALAPPDATA", ""), "TmTimeTracker", "state.db")
DEFAULT_OUT = os.path.join(REPO, "src", "TmTimeTracker", "Jev", "jev-estimator.json")
QUESTION_ID = "time"
FEATURES = ["log_expected_minutes", "log_middle_minutes", "confidence"]


def labelled_tickets(db):
    """Finished tickets - last seen in Review or Done, every cycle submitted - with 15+ minutes
    logged. The label is the total logged, rework included, because that is what Jira's Time
    Spent will be compared against."""
    c = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    rows = c.execute("""
        with t as (
          select ticket_key, sum(coalesce(submitted_minutes, 0)) submitted,
                 sum(case when submitted_at is null then 1 else 0 end) open_cycles
          from ticket_time group by ticket_key)
        select t.ticket_key, t.submitted,
               (select last_seen_status from ticket_time x where x.ticket_key = t.ticket_key
                order by cycle_started desc limit 1)
        from t where t.open_cycles = 0 and t.submitted >= 15""").fetchall()
    return {k: float(m) for k, m, status in rows if status in ("Review", "Done")}


def distribution(answer):
    items = sorted((int(o[1:]), p) for o, p in answer["probabilities"].items())
    minutes = np.array([i[0] for i in items], float)
    probs = np.array([i[1] for i in items], float)
    return minutes, probs / probs.sum()


def features(answer):
    """Must match JevEstimator.Features in C# exactly."""
    minutes, probs = distribution(answer)
    expected = float((minutes * probs).sum())
    middle = float(minutes[min(np.searchsorted(np.cumsum(probs), 0.5), len(minutes) - 1)])
    return [math.log(expected), math.log(middle), float(answer["confidence"])]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--answers", help="--jev-batch output for tools/jev/questions.json")
    ap.add_argument("--keys", help="write the labelled ticket keys to this file and stop")
    ap.add_argument("--db", default=DEFAULT_DB)
    ap.add_argument("--questions", default=os.path.join(HERE, "questions.json"))
    ap.add_argument("--out", default=DEFAULT_OUT)
    args = ap.parse_args()

    labels = labelled_tickets(args.db)
    if args.keys:
        open(args.keys, "w").write("\n".join(sorted(labels)) + "\n")
        print(f"Wrote {len(labels)} labelled ticket keys to {args.keys}")
        return
    if not args.answers:
        sys.exit("--answers is required unless --keys is given")

    questions = json.load(open(args.questions, encoding="utf-8"))
    answers = [json.loads(l) for l in open(args.answers, encoding="utf-8") if l.strip()]
    answers = [a for a in answers if a["key"] in labels]
    models = sorted({a["model"] for a in answers})

    X = np.array([features(a["answers"][QUESTION_ID]) for a in answers])
    minutes = np.array([labels[a["key"]] for a in answers])
    y = np.log(minutes)

    def pipeline():
        return make_pipeline(StandardScaler(), RidgeCV(alphas=np.logspace(-2, 3, 30)))

    jev_err, base_err = [], []
    for r in range(20):
        for tr, te in KFold(5, shuffle=True, random_state=r).split(y):
            jev_err += list(np.abs(np.exp(pipeline().fit(X[tr], y[tr]).predict(X[te])) - minutes[te]))
            base_err += list(np.abs(np.exp(np.median(y[tr])) - minutes[te]))

    fitted = pipeline().fit(X, y)
    scaler, ridge = fitted[0], fitted[-1]
    # Fold the scaler into the weights so C# needs only a dot product.
    coef = ridge.coef_ / scaler.scale_
    intercept = float(ridge.intercept_ - (ridge.coef_ * scaler.mean_ / scaler.scale_).sum())

    check = answers[0]
    check_minutes = math.exp(intercept + float(np.dot(coef, features(check["answers"][QUESTION_ID]))))

    out = {
        "model": models[-1],
        "question_id": QUESTION_ID,
        "questions": questions,
        "features": FEATURES,
        "intercept": intercept,
        "coefficients": [float(c) for c in coef],
        "fitted": {
            "date": date.today().isoformat(),
            "tickets": len(answers),
            "models_seen": models,
            "cv_mae_minutes": round(float(np.mean(jev_err)), 1),
            "median_guess_cv_mae_minutes": round(float(np.mean(base_err)), 1),
        },
        "check": {
            "ticket": check["key"],
            "answer": check["answers"][QUESTION_ID],
            "minutes": check_minutes,
        },
    }
    with open(args.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(out, f, indent=2)
        f.write("\n")

    print(f"Fitted on {len(answers)} tickets ({', '.join(models)}).")
    print(f"CV mean absolute error: {np.mean(jev_err):.1f} min vs {np.mean(base_err):.1f} for a median guess.")
    print(f"minutes = exp({intercept:.4f} + " + " + ".join(
        f"{c:.4f}*{n}" for c, n in zip(coef, FEATURES)) + ")")
    print(f"Check case {check['key']}: {check_minutes:.2f} min. Wrote {args.out}")


if __name__ == "__main__":
    main()
