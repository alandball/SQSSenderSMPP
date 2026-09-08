"""Loopback-only local SQS/DynamoDB bootstrap, synthetic producer and smoke oracle."""
import argparse
import json
import time
import uuid
from datetime import datetime, timezone
from pathlib import Path
from urllib.request import urlopen

import boto3

ENDPOINT = "http://127.0.0.1:5000"
QUEUE = f"{ENDPOINT}/123456789012/sender-local"
TABLE = "sender-local"


def client(service):
    return boto3.client(service, endpoint_url=ENDPOINT, region_name="us-east-1",
                        aws_access_key_id="local", aws_secret_access_key="local")


def bootstrap():
    sqs, db = client("sqs"), client("dynamodb")
    dlq = sqs.create_queue(QueueName="sender-local-dlq")["QueueUrl"]
    arn = sqs.get_queue_attributes(QueueUrl=dlq, AttributeNames=["QueueArn"])["Attributes"]["QueueArn"]
    sqs.create_queue(QueueName="sender-local", Attributes={
        "VisibilityTimeout": "120", "ReceiveMessageWaitTimeSeconds": "20",
        "RedrivePolicy": json.dumps({"deadLetterTargetArn": arn, "maxReceiveCount": 5})})
    if TABLE not in db.list_tables()["TableNames"]:
        db.create_table(TableName=TABLE, BillingMode="PAY_PER_REQUEST",
                        AttributeDefinitions=[{"AttributeName": "pk", "AttributeType": "S"}],
                        KeySchema=[{"AttributeName": "pk", "KeyType": "HASH"}])
        db.update_time_to_live(TableName=TABLE, TimeToLiveSpecification={"Enabled": True, "AttributeName": "expiresAt"})
    print("Local source queue, DLQ and submission ledger are ready.")


def envelope(children):
    return {"schemaVersion": "1.0", "envelopeId": str(uuid.uuid4()),
            "createdAt": datetime.now(timezone.utc).isoformat(),
            "messages": [{"smsId": str(uuid.uuid4()), "destination": f"+1555000{i:04d}",
                          "source": "LOCAL", "text": "Synthetic local sender test", "requestDeliveryReceipt": True}
                         for i in range(children)]}


def seed(count, children):
    sqs = client("sqs")
    for _ in range(count):
        sqs.send_message(QueueUrl=QUEUE, MessageBody=json.dumps(envelope(children)))
    print(f"Queued {count} envelopes / {count * children} synthetic SMS locally.")


def smoke():
    sqs, db = client("sqs"), client("dynamodb")
    message = envelope(50)
    before = json.load(urlopen("http://127.0.0.1:5081/stats"))["accepted"]
    started = time.monotonic()
    sqs.send_message(QueueUrl=QUEUE, MessageBody=json.dumps(message))
    deadline = started + 90
    while time.monotonic() < deadline:
        states = []
        for sms in message["messages"]:
            item = db.get_item(TableName=TABLE, Key={"pk": {"S": f'default/{sms["smsId"]}'}}, ConsistentRead=True).get("Item")
            states.append(item.get("state", {}).get("S") if item else None)
        attrs = sqs.get_queue_attributes(QueueUrl=QUEUE, AttributeNames=["ApproximateNumberOfMessages", "ApproximateNumberOfMessagesNotVisible"])["Attributes"]
        if all(state == "Accepted" for state in states) and all(value == "0" for value in attrs.values()):
            after = json.load(urlopen("http://127.0.0.1:5081/stats"))["accepted"]
            if after - before != 50:
                raise RuntimeError("Simulator acceptance count differs from ledger (run smoke on an idle harness).")
            report = {"children": 50, "accepted": 50, "sourceQueueEmpty": True,
                      "simulatorAcceptedDelta": after - before, "elapsedSeconds": round(time.monotonic() - started, 3)}
            Path("artifacts").mkdir(exist_ok=True)
            Path("artifacts/smoke.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
            print(json.dumps(report))
            return
        time.sleep(0.5)
    raise RuntimeError("Smoke failed: ledger, simulator and source queue did not reconcile within 90s.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=["bootstrap", "seed", "smoke"])
    parser.add_argument("--envelopes", type=int, default=1)
    parser.add_argument("--children", type=int, default=50)
    args = parser.parse_args()
    if not 1 <= args.children <= 50 or not 1 <= args.envelopes <= 50000:
        parser.error("children must be 1..50; envelopes must be 1..50000")
    if args.command == "bootstrap":
        bootstrap()
    elif args.command == "seed":
        seed(args.envelopes, args.children)
    else:
        smoke()
