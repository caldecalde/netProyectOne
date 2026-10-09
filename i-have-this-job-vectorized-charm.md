# Portfolio Project Plan — "CareFlow": Healthcare Data Exchange Platform (.NET 8 / AWS)

## Context
The goal is a portfolio project for the Bristlecone Mid-Level Backend .NET role that shows, in one repo with a public demo, almost every skill in the posting: .NET 8 / C# 12 / ASP.NET Core, microservices, event-driven design, secure APIs, healthcare standards (HL7 v2, X12 837/835, HIPAA-style safeguards), MongoDB, AWS (S3, Lambda, EC2, EKS), Kubernetes, CI/CD, SonarQube, gRPC, observability, and high-throughput performance work.

Decisions made: **hybrid hosting** (live demo on cheap AWS with k3s on EC2, plus EKS Terraform you can spin up for interviews), **4–6 week timeline**, **minimal UI** + Swagger/Postman.
The working folder `netProyectOne` is empty, so everything below is new.

## What the system does
A clinic or payer uploads healthcare files (HL7 ADT/ORU messages or X12 837 claims). The platform stores the raw file, parses and validates it, turns it into normalized JSON (patient, encounter, claim), and makes the data available through REST and gRPC. An X12 835 remittance can also be generated. All data is **synthetic** (from Synthea), and the README says so.

```
 [Blazor UI / Postman] --JWT--> [Ingestion API] --raw file--> S3 (SSE-KMS)
                                      | event: FileReceived (SNS->SQS)
                                      v
                              [Parser Worker] (HL7 via NHapi, X12 custom parser)
                                      | event: MessageParsed / ParseFailed
                                      v
        [Records Service] --MongoDB--  REST + gRPC query API
                                      ^
 S3 PutObject -> [Lambda: validator/quarantine] (envelope checks, PHI-safe audit)
```

## Repository layout (monorepo, Clean Architecture for each service)
```
careflow/
  src/
    BuildingBlocks/            # shared: events, OpenTelemetry setup, PHI log masking, Result<T>
    Ingestion/  (Api, Application, Domain, Infrastructure)
    Parser/     (Worker, Application, Domain[Hl7, X12], Infrastructure)
    Records/    (Api [REST + gRPC], Application, Domain, Infrastructure[Mongo])
    Lambda.Validator/
    Web/                       # Blazor WASM minimal UI
  tests/  (*.UnitTests [xUnit+Moq], *.IntegrationTests [Testcontainers], Architecture.Tests [NetArchTest])
  deploy/
    docker-compose.yml         # local: LocalStack (S3/SQS/SNS/Lambda), Mongo, Keycloak, Jaeger
    helm/careflow/             # one chart, values-local / values-k3s / values-eks
    terraform/  (modules: network, s3-kms, sqs-sns, lambda, ecr, ec2-k3s, eks[optional], cognito)
  scripts/ (bootstrap.ps1 + bootstrap.sh, seed-synthea.sh)
  postman/ (collection + environments; run in CI with Newman)
  perf/    (k6 scripts + results write-up)
  docs/    (ADRs, architecture diagram, HIPAA-safeguards.md)
  .github/workflows/ (ci.yml, cd.yml, sonar.yml)
```

## Tech choices (mapped to the job posting)
| Posting requirement | Implementation |
|---|---|
| .NET 8, C# 12, ASP.NET Core | All services on `net8.0`. Use C# 12 features: primary constructors, collection expressions. Use Minimal APIs and Controllers where each fits. (Note: .NET 8 support ends Nov 2026. Add an ADR about the upgrade path to .NET 10.) |
| Microservices / event-driven | 3 services + Lambda, linked by SNS→SQS. Transactional outbox in Ingestion, idempotent consumers, DLQ + retry with Polly. |
| Secure APIs, authN/authZ | JWT bearer from Keycloak locally and Cognito on AWS. Policy-based authorization (roles `submitter`, `viewer`, `auditor`). Rate limiting, input size limits, security headers. |
| HIPAA | Synthetic data only. Encryption in transit (TLS) and at rest (KMS). PHI masking in logs (custom Serilog enricher). Immutable audit trail collection. Least-privilege IAM. Written up in `HIPAA-safeguards.md`. |
| HL7 / X12 | HL7 v2 via **NHapi** (ADT^A01/A08, ORU^R01). Hand-written **X12 parser** (ISA/GS/ST envelope, 837P loops, 999 acknowledgment, 835 generation). The custom parser shows real understanding of the format. |
| MongoDB / NoSQL | Records service on MongoDB (Atlas M0 free tier in the demo), with indexes, aggregation queries, and schema versioning. |
| gRPC | Records exposes `RecordsQuery` over gRPC. Ingestion calls it to detect duplicates. JSON transcoding gives the same calls as REST. |
| Profiling / millions of tx | k6 load test plus a BenchmarkDotNet suite for the parsers. Span/`ArrayPool`-based X12 tokenizer, batching of Mongo writes, `dotnet-counters`/`dotnet-trace` findings written up in `perf/README.md` (before/after numbers). |
| xUnit, Moq, Postman | Unit tests (Moq for ports), integration tests (Testcontainers + `WebApplicationFactory`), Newman runs the Postman collection in CI. |
| Observability (Dynatrace, CloudWatch) | OpenTelemetry traces, metrics, and logs. Jaeger locally; on AWS, an ADOT collector sends to **CloudWatch** (Logs, metrics, X-Ray). Optional: Dynatrace free trial via OTLP exporter (one config switch). |
| Windows + Linux, PowerShell + Bash | Bootstrap and seed scripts in both `.ps1` and `.sh`. CI runs on `ubuntu-latest` + `windows-latest` matrix for tests. |
| AWS S3, Lambda, EC2, EKS | S3 for raw files, Lambda validator, EC2 running **k3s** (live demo), EKS module (`enable_eks=true`) for interviews. |
| Kubernetes / containers | Dockerfiles (chiseled .NET images), one Helm chart, HPA, readiness/liveness probes, ConfigMaps/Secrets. |
| CI/CD | GitHub Actions: build → test → SonarCloud quality gate → docker build → push to ECR/GHCR → `helm upgrade` to k3s. Terraform plan runs on each PR. |
| SonarQube | SonarCloud (free for public repos) with coverage via Coverlet, plus a badge in the README. |
| Clean Arch / SOLID / DRY | Layered projects, NetArchTest rules that enforce dependency direction, ADRs. |

## Phases (≈ 5–6 weeks)
1. **Week 1 – Foundation**: solution skeleton, BuildingBlocks, docker-compose (LocalStack, Mongo, Keycloak, Jaeger), Ingestion API with JWT and S3 upload, CI with build + unit tests + SonarCloud.
2. **Week 2 – Parsing**: HL7 parsing via NHapi, custom X12 837 tokenizer and parser, 999 ack, SNS/SQS events with outbox and idempotency, DLQ. Heavy unit tests using Synthea-generated samples.
3. **Week 3 – Records + gRPC**: Records service on Mongo, REST + gRPC + JSON transcoding, 835 generation, integration tests with Testcontainers, Postman collection + Newman in CI.
4. **Week 4 – Cloud & K8s**: Lambda validator, Terraform (S3/KMS, SQS/SNS, Lambda, ECR, Cognito, EC2 k3s), Helm chart, CD pipeline deploys to k3s. Optional EKS module tested once, then destroyed.
5. **Week 5 – Observability & performance**: OpenTelemetry → CloudWatch/X-Ray, dashboards, alarms, k6 load test, BenchmarkDotNet, a profiling write-up with before/after numbers (target: show a throughput figure that extrapolates to millions/day).
6. **Week 6 – Polish & demo**: Blazor UI (upload a file → status timeline → parsed JSON view), README with architecture diagram, badges, live demo link, a 3–5 minute walkthrough video, ADRs, `HIPAA-safeguards.md`.

## Public demo setup (low cost)
- 1 × EC2 `t4g.medium` (or `t4g.small`) running k3s + Caddy for TLS, at a free subdomain via DuckDNS or a cheap domain. Cost: about $12–25/month. You can stop it between interviews.
- S3, SQS/SNS, Lambda, Cognito, and CloudWatch fit within or close to the free tier. MongoDB Atlas M0 is free.
- Demo login: a read-only `viewer` account shown in the README, and an upload rate limit to prevent abuse.
- `terraform destroy` removes everything. Set an AWS Budget alarm at $20.

## Verification
- `scripts/bootstrap.ps1` / `.sh` → `docker compose up` → upload the sample HL7 and X12 files via Swagger/UI → records are queryable through REST and gRPC (`grpcurl`), and traces show up in Jaeger.
- `dotnet test` passes on Windows and Linux (unit, integration with Testcontainers, architecture tests).
- CI is green: SonarCloud quality gate passes, Newman Postman run passes, images are pushed.
- On AWS: CD deploys to k3s, the public URL works end to end, the S3 upload triggers the Lambda, and traces and logs appear in CloudWatch/X-Ray.
- `perf/README.md` holds the k6 and BenchmarkDotNet results.

## First implementation step (once approved)
Create the solution skeleton in `netProyectOne/` (`dotnet new sln`, the projects above, `Directory.Build.props` with `net8.0`/`LangVersion 12`/nullable/analyzers), docker-compose, and the first CI workflow. Then build Ingestion API end to end.
