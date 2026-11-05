"""Train a demonstrator on synthetic operating regimes; no production data or DB writes."""
from pathlib import Path
import json, hashlib
import numpy as np
from sklearn.ensemble import RandomForestClassifier
from sklearn.metrics import classification_report
from sklearn.model_selection import train_test_split
rng=np.random.default_rng(20261106)
n=16000
active=rng.integers(0,2,n)
power=rng.integers(0,2,n)
heat=rng.integers(0,2,n)
# Independent synthetic operating examples; labels reflect the generated condition.
current=np.where(active==1,rng.uniform(.02,32,n),np.where(power==1,rng.uniform(.7,26,n),rng.uniform(0,.18,n)))
temp=np.where(heat==1,rng.uniform(36.5,48,n),rng.uniform(18,34.5,n))
humidity=rng.uniform(30,85,n)
x=np.column_stack([temp,humidity,current,active])
y=np.where(heat==1,2,0)+np.where((active==0)&(power==1),1,0)
a,c,ya,yc=train_test_split(x,y,test_size=.25,random_state=42,stratify=y)
model=RandomForestClassifier(n_estimators=40,max_depth=6,min_samples_leaf=8,random_state=42,n_jobs=-1).fit(a,ya)
trees=[]
for estimator in model.estimators_:
 t=estimator.tree_
 values=t.value[:,0,:];values=values/values.sum(axis=1,keepdims=True)
 trees.append(dict(left=t.children_left.tolist(),right=t.children_right.tolist(),feature=t.feature.tolist(),threshold=t.threshold.tolist(),value=values.tolist()))
vectors=[[28,65,.08,0],[28,65,1.6,0],[25,57,26,1],[41,48,.08,0],[41,48,12,0]]
root=dict(schemaVersion=1,modelVersion=hashlib.sha256(json.dumps(trees).encode()).hexdigest()[:16],
 trainingSource="SYNTHETIC",algorithm="RandomForestClassifier",features=["temperatureC","humidityPercent","currentAmperes","inUse"],
 classes=["NORMAL","POWER_AFTER_USE","HIGH_TEMPERATURE","POWER_AND_HEAT"],trees=trees,
 metrics=classification_report(yc,model.predict(c),output_dict=True),
 goldenVectors=[dict(input=v,probabilities=model.predict_proba([v])[0].tolist()) for v in vectors],
 limitations="Synthetic holdout only. Not field-validated. Aggregate current cannot identify individual appliances. No occupancy claim.")
target=Path(__file__).resolve().parents[2]/"ClassHub-API/Models/iot_forest.json"
target.write_text(json.dumps(root,separators=(",",":")),encoding="utf-8")
print(json.dumps(dict(modelVersion=root["modelVersion"],syntheticAccuracy=root["metrics"]["accuracy"],trainingSamples=len(a),testSamples=len(c))))
