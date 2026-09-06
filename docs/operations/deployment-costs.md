# Deployment cost estimate

This is a reproducible **public retail estimate, not a measured bill or a deployment quote**.
Rates were retrieved without authentication from Microsoft's Azure Retail Prices API on
**2026-09-06 UTC**, in **USD**, for **West US 2 (`westus2`)**, Consumption/pay-as-you-go pricing.
Global meters are identified below. No Azure resources were created. Contract discounts, tax,
reservations, and savings plans are excluded. Refresh the rates before approving expenditure.

## Topology and rate evidence

The initial topology uses a Consumption Container Apps environment, a web replica allocation of
0.5 vCPU / 1 GiB with minimum 0 and maximum 3 replicas, and a scheduled 0.5 vCPU / 1 GiB worker
once per minute. SQL is provisioned Standard S0; it does not stop billing when web replicas reach
zero. Three private endpoints serve SQL, Blob, and Key Vault, with three private DNS zones.
The foundation uses Standard LRS Blob, Standard Key Vault, and Log Analytics PerGB2018.

Each value below is an actual `retailPrice` returned by the linked API query, not a price inferred
from another region. `effectiveStartDate` is the meter's start date, not the date it was retrieved.
The API's pagination links were empty for the queried result sets. Its fields and filtering are
specified in the [Retail Prices API documentation](https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices).

| Product / SKU / meter | USD retail rate | Effective start | Region / source |
| --- | ---: | --- | --- |
| Azure Container Apps / Standard / vCPU Active Usage | 0.000034 per vCPU-second | 2022-06-01 | westus2, [API][aca] |
| Azure Container Apps / Standard / Memory Active Usage | 0.000004 per GiB-second | 2022-06-01 | westus2, [API][aca] |
| Azure Container Apps / Standard / Requests | 0.40 per million | 2022-06-01 | westus2, [API][aca] |
| SQL Database Single Standard / S0 / S0 DTUs | 0.4839 per day | 2014-11-01 | westus2, [API][sql] |
| Virtual Network Private Link / Standard / Private Endpoint | 0.01 per endpoint-hour | 2020-02-01 | Global, [API][private] |
| Virtual Network Private Link / Standard / Data Processed, ingress and egress | 0.01 per GB in each direction, first tier | 2021-11-01 | Global, [API][private] |
| Azure DNS / Private / Zone | 0.50 per zone-month, first 25 zones | 2019-12-01 | global meter, empty region field, [API][dns] |
| Azure DNS / Private / Queries | 0.40 per million queries | 2019-12-01 | global meter, empty region field, [API][dns] |
| General Block Blob v2 / Hot LRS / Data Stored | 0.0184 per GB-month, first 51,200 GB | 2017-02-03 | westus2, [API][blob] |
| General Block Blob v2 / Hot LRS / Write Operations | 0.05 per 10,000 operations | 2016-07-11 | westus2, [API][blob] |
| General Block Blob v2 / Hot LRS / Read Operations | 0.004 per 10,000 operations | 2016-07-11 | westus2, [API][blob] |
| General Block Blob v2 / Hot LRS / List and Create Container Operations | 0.05 per 10,000 operations | 2018-12-01 | westus2, [API][blob] |
| Key Vault / Standard / Operations | 0.03 per 10,000 operations | 2015-08-01 | westus2, [API][vault] |
| Log Analytics / Analytics Logs / Data Ingestion | 2.30 per GB above the API's 5 GB tier threshold | 2018-02-01 | westus2, [API][logs] |
| Log Analytics / Analytics Logs / Data Retention | 0.10 per GB-month of chargeable retained data | 2018-02-01 | westus2, [API][logs] |
| Azure App Service Premium v3 Plan - Linux / P1 v3 / App | 0.155 per instance-hour | 2020-10-01 | westus2, [API][appservice] |

The DNS API calls the zone unit `1`; Microsoft's [DNS pricing page](https://azure.microsoft.com/en-us/pricing/details/dns/)
identifies it as a monthly zone charge, prorated daily. Private endpoint data processing counts
traffic in both directions; it is separate from other applicable bandwidth charges.
See [Private Link billing](https://azure.microsoft.com/en-us/pricing/details/private-link/).

Container Apps grants 180,000 vCPU-seconds, 360,000 GiB-seconds and two million external requests
per subscription per calendar month. Jobs use active rates. With minimum replicas set to zero,
remaining running replicas use active rates; scale-to-zero eliminates their compute usage only.
These grants are shared with other apps/jobs in the subscription, not allocated per Workbench host.
See [Container Apps billing](https://learn.microsoft.com/en-us/azure/container-apps/billing).

The API also returns $0.10/hour ACA environment management and environment-private-endpoint meters
(effective 2026-09-01). The checked-in environment has only a Consumption profile and no private
endpoint on the ACA environment itself. The three dependency endpoints are not ACA ingress private
endpoints. No environment feature surcharge is assumed here. Enabling dedicated profiles, ACA
private ingress, or planned maintenance requires checking the applicable management/feature charges;
$0.10/hour is $72 in the 720-hour example month. The billing documentation describes these conditional
charges; the mere presence of a retail meter does not establish that this deployment incurs it.

## Estimator and fixed recurring component

Use a synthetic 30-day month: `D = 30`, `H = 720`, scheduled executions `J = 43,200`.
Let `W` be total web replica-seconds across every active revision, and `t` the average **whole job
replica lifetime**, including startup and shutdown. The 45-second drain setting is not that lifetime.
Retries, overlap, migration jobs, and deployment warm-up add their actual replica-seconds.

For the chosen 0.5 vCPU / 1 GiB allocations:

```text
S = W + J*t + additional replica-seconds
ACA compute = max(0, 0.5*S - available CPU grant) * 0.000034
            + max(0, S - available memory grant) * 0.000004
ACA requests = max(0, external requests - available request grant) / 1,000,000 * 0.40
SQL S0 = D * 0.4839
Private endpoints = 3 * H * 0.01
Private DNS zones = 3 * 0.50
```

The quoted **SQL + endpoint + DNS recurring component is $37.62 per 30-day month**:
`14.517 + 21.60 + 1.50 = 37.617`. This is a partial infrastructure floor, not a total monthly bill.
Storage, traffic, jobs, logging and the exclusions below remain payable even with no web requests.

The following are **unmeasured sensitivity cases**, not throughput forecasts. They assume the full
ACA grants remain available and fewer than two million external requests. The final column removes
the grants entirely to show the effect of sharing a subscription.

| Synthetic activity over 30 days | Web replica-seconds | Mean job lifetime | ACA compute with full grants | ACA compute with no grants |
| --- | ---: | ---: | ---: | ---: |
| No web replicas; short empty jobs | 0 | 5 seconds | $0.00 | $4.54 |
| One web replica for two hours/day | 216,000 | 5 seconds | $1.51 | $9.07 |
| Same web usage; longer jobs | 216,000 | 45 seconds | $37.80 | $45.36 |
| Three web replicas running continuously | 7,776,000 | 5 seconds | $160.27 | $167.83 |

After grants are consumed, each additional average second per every-minute worker run adds about
`43,200 * 0.000021 = $0.9072/month`. Each extra web replica-hour adds `$0.0756`. This makes actual
job startup time, revision overlap, and replica residence time essential measurements.

Add usage with these first-tier formulas:

```text
Blob = stored GB-month * 0.0184
     + writes / 10,000 * 0.05 + reads / 10,000 * 0.004
     + list/create-container operations / 10,000 * 0.05
Private Link traffic = (ingress GB + egress GB) * 0.01
DNS queries = queries / 1,000,000 * 0.40
Key Vault = standard operations / 10,000 * 0.03
Logs = chargeable ingestion GB * 2.30 + chargeable retained GB-month * 0.10
```

For illustration only, 10 GB-month Blob, 10,000 writes, 10,000 reads, one million DNS queries,
10,000 vault operations, 100 GB total Private Link traffic, and 1 GB chargeable log ingestion add
$3.968 before list operations and chargeable retention. Added to the two-hours/day, five-second-job
case and the fixed component, the **priced-components subtotal is $43.10**. None of these usage
quantities has been observed in a hosted environment. Log grant eligibility and included retention
must be checked for the subscription/table plan; the example intentionally assumes the 1 GB is chargeable.

Excluded, without assuming they are free: registry hosting/builds and image transfer, internet or
cross-region egress, SMTP service, domains, backup capacity and recovery copies, extended SQL backup
retention, alert rules/action notifications, support, private build/migration runner compute, and any
new networking appliances or environment features. SQL S0 performance has not been measured against
Workbench's workload. A larger SQL SKU changes the floor. Retained blob versions and snapshots count
toward billed capacity; the example's 10 GB must include them.

## Linux App Service comparison

Linux Premium v3 P1v3 provides 2 vCPU and 8 GB RAM, exceeding the ACA web peak allocation of
1.5 vCPU / 3 GiB across three replicas. A continuously allocated P1v3 instance costs
`720 * 0.155 = $111.60/month`; two cost $223.20. Capacity figures are from Microsoft's
[Linux App Service plan table](https://azure.microsoft.com/en-us/pricing/details/app-service/linux/).
The Linux rate is explicitly selected; the similarly named Windows meter is excluded.

This is an allocation comparison, not a claim of equivalent throughput or availability. One App
Service instance is one web failure domain; two instances provide a separate redundancy comparison.
ACA's three-replica maximum does not mean three replicas are continuously available, and minimum zero
includes cold starts. Keep the same SQL, private endpoints, Blob, vault, logging, and **scheduled ACA
worker** in this comparison, so worker scheduling is not silently omitted or assumed to be provided
by Linux custom-container WebJobs. The App Service alternative would require its own VNet integration
and deployment validation; it is not implemented by the current Bicep.

In the light synthetic case, the web allocation itself costs $4.54 before grants on ACA versus
$111.60 on one P1v3. In the continuously running three-replica case, web-only ACA compute is $163.30
before grants. Shared grant allocation and worker usage affect the final difference. These figures
support measuring duty cycle before selecting a permanent plan; they do not establish a capacity SLA.

## Refresh and hosted acceptance

Before a hosted rollout, repeat the linked queries and retain the returned `productName`, `skuName`,
`meterName`, `retailPrice`, `currencyCode`, `unitOfMeasure`, `tierMinimumUnits`, `armRegionName`,
`effectiveStartDate`, and `type`. Follow `NextPageLink` if present and select the intended tier and OS.
The documented API is public and requires no subscription credentials.

Replace synthetic inputs with measured cold-start latency, web replica-seconds, job lifetime and
retries, backlog throughput, SQL utilization, log volume, Blob growth/version retention, and network
transfer. Reconcile one representative billing window with Cost Management, including feature
surcharges and shared grants. A hosted cost conclusion remains pending those authorized measurements.

[aca]: https://prices.azure.com/api/retail/prices?$filter=armRegionName%20eq%20%27westus2%27%20and%20serviceName%20eq%20%27Azure%20Container%20Apps%27%20and%20priceType%20eq%20%27Consumption%27
[sql]: https://prices.azure.com/api/retail/prices?$filter=armRegionName%20eq%20%27westus2%27%20and%20serviceName%20eq%20%27SQL%20Database%27%20and%20skuName%20eq%20%27S0%27%20and%20priceType%20eq%20%27Consumption%27
[private]: https://prices.azure.com/api/retail/prices?$filter=armRegionName%20eq%20%27Global%27%20and%20productName%20eq%20%27Virtual%20Network%20Private%20Link%27%20and%20priceType%20eq%20%27Consumption%27
[dns]: https://prices.azure.com/api/retail/prices?$filter=serviceName%20eq%20%27Azure%20DNS%27%20and%20skuName%20eq%20%27Private%27%20and%20priceType%20eq%20%27Consumption%27
[blob]: https://prices.azure.com/api/retail/prices?$filter=armRegionName%20eq%20%27westus2%27%20and%20productName%20eq%20%27General%20Block%20Blob%20v2%27%20and%20skuName%20eq%20%27Hot%20LRS%27%20and%20priceType%20eq%20%27Consumption%27
[vault]: https://prices.azure.com/api/retail/prices?$filter=armRegionName%20eq%20%27westus2%27%20and%20serviceName%20eq%20%27Key%20Vault%27%20and%20skuName%20eq%20%27Standard%27%20and%20priceType%20eq%20%27Consumption%27
[logs]: https://prices.azure.com/api/retail/prices?$filter=armRegionName%20eq%20%27westus2%27%20and%20serviceName%20eq%20%27Log%20Analytics%27%20and%20skuName%20eq%20%27Analytics%20Logs%27%20and%20priceType%20eq%20%27Consumption%27
[appservice]: https://prices.azure.com/api/retail/prices?$filter=armRegionName%20eq%20%27westus2%27%20and%20productName%20eq%20%27Azure%20App%20Service%20Premium%20v3%20Plan%20-%20Linux%27%20and%20skuName%20eq%20%27P1%20v3%27%20and%20priceType%20eq%20%27Consumption%27
