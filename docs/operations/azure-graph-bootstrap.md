# Microsoft 365 setup for Workbench notifications

Use this only when `deliveryProvider=Graph`. Azure identity creation and Exchange changes require
operator approval. This procedure does not grant tenant-wide Graph application permissions.
Prerequisites are the organization's verified mail domain, Exchange Online administration access,
and the dedicated mail managed identity from [host preparation](azure-bootstrap-host.md).

Connect with the supported ExchangeOnlineManagement module using `Connect-ExchangeOnline`.
Set `$sender` to the configured no-reply address and `$personal` to an existing personal mailbox for
the negative check. Set `$mailClientId` and `$mailPrincipalId` from `az identity show` for the mail
identity, not the registry pull identity. Choose unique `$scopeName` and `$assignmentName` values.

Create the shared mailbox using **Microsoft 365 admin center → Teams & groups → Shared mailboxes →
Add a shared mailbox** with the chosen display name and sender address. Then read back:

```powershell
Get-Mailbox -Identity $sender | Format-List DisplayName,PrimarySmtpAddress,RecipientTypeDetails,ExternalDirectoryObjectId
```

Require SharedMailbox and the exact address. Copy `ExternalDirectoryObjectId` to `graphMailboxId`;
`graphManagedIdentityClientId` is the managed identity's client ID. Keep interactive sign-in for the
shared mailbox blocked. A shared mailbox need not appear in your Outlook to send via the application.

The Exchange assignment follows Microsoft's [application RBAC procedure](https://learn.microsoft.com/en-us/exchange/permissions-exo/application-rbac).
Inspect existing objects before creating duplicates. A fresh setup uses:

```powershell
Get-OrganizationConfig | Format-List IsDehydrated
# Only if IsDehydrated is True, and after approval:
# Enable-OrganizationCustomization
New-ManagementScope -Name $scopeName -RecipientRestrictionFilter "PrimarySmtpAddress -eq '$sender'"
$scope = Get-ManagementScope -Identity $scopeName
$matched = @(Get-Recipient -RecipientPreviewFilter $scope.RecipientFilter)
if ($matched.Count -ne 1 -or [string]$matched[0].PrimarySmtpAddress -ne $sender) {
    throw 'Mail scope must match exactly the intended shared mailbox.'
}
New-ServicePrincipal -AppId $mailClientId -ObjectId $mailPrincipalId -DisplayName 'Workbench Notifications'
New-ManagementRoleAssignment -Name $assignmentName -Role 'Application Mail.Send' `
    -App $mailPrincipalId -CustomResourceScope $scopeName
Test-ServicePrincipalAuthorization -Identity $mailPrincipalId -Resource $sender |
    Format-Table RoleName,GrantedPermissions,InScope
Test-ServicePrincipalAuthorization -Identity $mailPrincipalId -Resource $personal |
    Format-Table RoleName,GrantedPermissions,InScope
```

Require Mail.Send True for sender and False for personal mailbox. An assignment failure about
delegation is an Exchange administrative-rights problem: inspect effective membership/delegation
for Application Mail.Send and Role Management, reconnect, then contact Microsoft support if the
effective grants still disagree with authorization. Do not work around it with organization-wide
Graph grants. Register the service principal before assigning its role. See the official procedure
for required administrator roles; global administrator UI selection alone is not the assignment proof.

In the operator Azure CLI session, inspect the mail identity's independent Entra grants:

```powershell
az rest --method get --url "https://graph.microsoft.com/v1.0/servicePrincipals/${mailPrincipalId}/appRoleAssignments" --query 'value[].{role:appRoleId,resource:resourceDisplayName}'
```

For this dedicated identity expect none; investigate any returned grant before delivery acceptance.
Exchange scope does not constrain separate Entra grants. Keep runtime worker retries bounded during
propagation. Set `mailIdentityId` to the Azure identity resource ID in the installation parameters.

To reject replies, create an Exchange **Mail flow → Rules** rule matching recipient `$sender` and
reject the message with an explanation that the address does not receive mail. Apply to internal and
external senders; choose Enforce, an audit severity such as Low, and verify that no exception admits
unwanted mail. Sender-address matching is not the recipient condition. Send a controlled incoming
message and require a rejection report.

Finally, run the released Workbench worker manually with a queued recovery/invitation test addressed
to the operator. Require receipt, not just Graph 202 or job success. Separately test a send using the
personal mailbox as sender with the mail identity and require 403/ErrorAccessDenied. The test must
use that managed identity (for example from the temporary host), not the operator's Graph login.
On that host, set/export nonsecret `MAIL_CLIENT_ID`, `SENDER` (shared mailbox ID/address), `PERSONAL`
(personal mailbox address), and `TEST_RECIPIENT` (the operator's address), then run this bounded test
after approval of the test messages. It outputs codes only; neither the token nor message is logged:

```bash
set -euo pipefail
python3 - <<'PY'
import json, os, urllib.request, urllib.parse, urllib.error
query = urllib.parse.urlencode({'api-version': '2018-02-01',
    'resource': 'https://graph.microsoft.com/', 'client_id': os.environ['MAIL_CLIENT_ID']})
request = urllib.request.Request('http://169.254.169.254/metadata/identity/oauth2/token?'+query,
    headers={'Metadata': 'true'})
token = json.load(urllib.request.urlopen(request, timeout=15))['access_token']
payload = json.dumps({'message': {'subject': 'Workbench mail authorization test',
    'body': {'contentType': 'Text', 'content': 'Controlled setup verification.'},
    'toRecipients': [{'emailAddress': {'address': os.environ['TEST_RECIPIENT']}}]},
    'saveToSentItems': False}).encode()
for sender, expected in [(os.environ['SENDER'], 202), (os.environ['PERSONAL'], 403)]:
    url = 'https://graph.microsoft.com/v1.0/users/'+urllib.parse.quote(sender, safe='')+'/sendMail'
    req = urllib.request.Request(url, data=payload, method='POST',
        headers={'Authorization': 'Bearer '+token, 'Content-Type': 'application/json'})
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            code = response.status
    except urllib.error.HTTPError as error:
        code = error.code
    print('Graph status:', code)
    if code != expected:
        raise SystemExit('Mail authorization test failed; inspect permissions before retrying.')
PY
```

Require the first message to arrive. This host test supplements rather than replaces the released
worker/queue test above. Do not automatically retry an ambiguous send timeout.
Retain only HTTP/error codes, never bearer tokens or message/recovery bodies in logs. Enable public
recovery and the worker schedule only after the durable-queue delivery check passes.
