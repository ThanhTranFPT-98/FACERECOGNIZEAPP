vector_file = "datasets/face_features/feature.npz"

import numpy as np
import os

read_features = np.load(vector_file)


print(read_features["images_name"])
print(read_features["images_emb"])
index = []

for i in range(len(read_features["images_name"])):
    if read_features["images_name"][i] == 'thanh':
        index.append(i)



file_name = "datasets/face_features/vectors.txt"
with open(file_name,"w") as f:
    for i in index:
        f.write(str(read_features["images_emb"][i])+"\n")