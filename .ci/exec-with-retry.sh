#!/bin/bash -x

# Try 10 times
for iter in {1..10}
do
	("$@")
	if [[ $? -eq 0 ]]
	then
		exit 0
	fi
done
