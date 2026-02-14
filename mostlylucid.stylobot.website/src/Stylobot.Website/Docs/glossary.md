# Glossary

## Bot probability
A score between 0 and 1 estimating how likely a request is automated.

## Confidence
How strong and internally consistent the evidence is for the current classification.

## Risk band
A human-readable category derived from score and signals.

## Policy
A configured set of detectors and response behavior.

## Detector contribution
A weighted evidence item produced by a detector, including reason text and confidence delta.

## Fast path
Low-latency contributors that run first on a request.

## Learning
Feedback loop that adjusts reputation and weighting over time.

## Dashboard
UI stream of recent events, reasons, and trends.

## Gateway mode
Running Stylobot in front of your app as a reverse proxy.

## Recommended action
The policy output (`Allow`, `Throttle`, `Challenge`, `Block`) derived from risk and confidence.
