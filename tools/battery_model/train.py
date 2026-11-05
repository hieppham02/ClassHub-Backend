"""Train an experimental battery RUL regressor. Never writes to a database."""
import argparse, hashlib, json
from pathlib import Path
from datetime import datetime, timezone
import numpy as np
import sklearn
from sklearn.ensemble import GradientBoostingRegressor
from sklearn.model_selection import GroupShuffleSplit
from sklearn.metrics import mean_absolute_error, mean_squared_error, r2_score

SEED=20261106
FEATURES=["deviceKind","ageDays","borrowHoursTotal","borrowEventsTotal","hoursLast7","activeDaysLast7","rechargeable"]
def generate():
    rng=np.random.default_rng(SEED); features=[]; targets=[]; groups=[]; lifetimes=[]
    for device in range(600):
        kind=device%2; rechargeable=int(rng.random()<.35)
        for cycle in range(2):
            # Explicit synthetic assumptions, NOT measured battery specifications.
            capacity=rng.uniform(18,50) if kind==0 else rng.uniform(400,1600)
            capacity*=.75 if rechargeable else 1
            calendar=rng.uniform(150,300) if kind==0 else rng.uniform(300,600)
            daily=float(rng.uniform(.15,4.5)); hours=[]; events=[]; hazard=0
            for day in range(1,731):
                h=min(12,float(rng.lognormal(np.log(daily),.5))) if rng.random()<.75 else 0
                hours.append(h);events.append(int(rng.poisson(h*1.2)) if h else 0)
                hazard+=1/calendar+h/capacity
                if hazard>=1:break
            end=len(hours);cumulative=np.cumsum(hours); event_sum=np.cumsum(events)
            samples=sorted(set([0]+list(range(3,end,max(3,end//10)))))
            for age in samples:
                if age>=end:continue
                recent=hours[max(0,age-7):age]
                features.append([kind,age,float(cumulative[age-1]) if age else 0,
                    int(event_sum[age-1]) if age else 0,sum(recent),sum(x>0 for x in recent),rechargeable])
                targets.append(end-age);groups.append(device);lifetimes.append(end)
    return np.asarray(features),np.asarray(targets),np.asarray(groups),np.asarray(lifetimes)
def export(model):
    trees=[]
    for estimator in model.estimators_[:,0]:
        t=estimator.tree_
        trees.append(dict(left=t.children_left.tolist(),right=t.children_right.tolist(),
            feature=t.feature.tolist(),threshold=t.threshold.tolist(),value=t.value[:,0,0].tolist()))
    return dict(initial=float(model.init_.constant_[0,0]),learningRate=model.learning_rate,trees=trees)
def predict_export(m,x):
    x=np.asarray(x,dtype=np.float32); result=m["initial"]
    for t in m["trees"]:
        n=0
        while t["left"][n]!=-1:n=t["left"][n] if x[t["feature"][n]]<=t["threshold"][n] else t["right"][n]
        result+=m["learningRate"]*t["value"][n]
    return float(np.clip(result,0,730))
def main():
    parser=argparse.ArgumentParser();parser.add_argument("--output",type=Path,default=Path(__file__).resolve().parents[2]/"ClassHub-API/Models/battery_gbr.json")
    args=parser.parse_args()
    x,y,groups,lifetimes=generate()
    train,rest=next(GroupShuffleSplit(n_splits=1,test_size=.3,random_state=SEED).split(x,y,groups))
    a,b=next(GroupShuffleSplit(n_splits=1,test_size=.5,random_state=SEED+1).split(x[rest],y[rest],groups[rest]))
    calibration,test=rest[a],rest[b]
    assert not(set(groups[train])&set(groups[test]) or set(groups[train])&set(groups[calibration]) or set(groups[test])&set(groups[calibration]))
    model=GradientBoostingRegressor(n_estimators=180,max_depth=3,min_samples_leaf=12,learning_rate=.06,loss="huber",random_state=SEED)
    model.fit(x[train],y[train]);prediction=np.clip(model.predict(x[test]),0,730)
    mean_life={k:float(np.mean(lifetimes[train][x[train,0]==k])) for k in (0,1)}
    baseline=np.asarray([max(0,mean_life[int(row[0])]-row[1]) for row in x[test]])
    residual=np.abs(y[calibration]-np.clip(model.predict(x[calibration]),0,730))
    errors=[float(np.quantile(residual[x[calibration,0]==k],.9,method="higher")) for k in (0,1)]
    core=export(model)
    metrics=dict(maeDays=float(mean_absolute_error(y[test],prediction)),rmseDays=float(np.sqrt(mean_squared_error(y[test],prediction))),
        r2=float(r2_score(y[test],prediction)),baselineMaeDays=float(mean_absolute_error(y[test],baseline)),
        microphoneMaeDays=float(mean_absolute_error(y[test][x[test,0]==0],prediction[x[test,0]==0])),
        remoteMaeDays=float(mean_absolute_error(y[test][x[test,0]==1],prediction[x[test,0]==1])),
        testIntervalCoverage=float(np.mean(np.abs(y[test]-prediction)<=np.asarray(errors)[x[test,0].astype(int)])))
    meta=dict(algorithm="GradientBoostingRegressor",trainingSource="SYNTHETIC",seed=SEED,
        trainedAtUtc=datetime.now(timezone.utc).isoformat(),sklearnVersion=sklearn.__version__,
        sampleCount=len(x),trainRows=len(train),calibrationRows=len(calibration),testRows=len(test),
        deviceCount=len(set(groups)),split="70/15/15 by device; no shared device across splits",features=FEATURES,
        metrics=metrics,intervalAbsoluteErrorDays=errors,featureImportance=dict(zip(FEATURES,model.feature_importances_.tolist())),
        realBatteryCycles=0,limitation="Experimental model trained on generated cycles; not validated on real battery failures.",
        calendarBaselineDays=[mean_life[0],mean_life[1]],featureMinimum=x.min(axis=0).tolist(),featureMaximum=x.max(axis=0).tolist())
    scenarios=[[0,7,12,16,8,5,0],[0,7,28,35,20,7,0],[1,100,80,150,8,5,0]]
    vectors=[dict(features=row,expected=predict_export(core,row)) for row in scenarios]
    for row in x[test][:100]:
        assert abs(predict_export(core,row)-float(np.clip(model.predict([row])[0],0,730)))<1e-7
    artifact=dict(schemaVersion=1,metadata=meta,model=core,goldenVectors=vectors)
    artifact["modelVersion"]=hashlib.sha256(json.dumps(artifact,sort_keys=True).encode()).hexdigest()[:20]
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(artifact,separators=(",",":"),allow_nan=False),encoding="utf-8")
    report=args.output.parents[2]/"docs/ai-battery-training.json";report.parent.mkdir(exist_ok=True)
    report.write_text(json.dumps(dict(modelVersion=artifact["modelVersion"],**meta),indent=2,ensure_ascii=False),encoding="utf-8")
    print(json.dumps(dict(modelVersion=artifact["modelVersion"],samples=len(x),metrics=metrics,goldenVectors=vectors),indent=2))
if __name__=="__main__":main()
