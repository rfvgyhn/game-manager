# Event grid webhooks
The following examples use the Azure CLI. The same things can be done manually via the Azure Portal if you prefer.

Adding support for Azure Event Grid events and Initialization events requires the following:
- A Microsoft Entra ID Enterprise Application/App Registration. This provides azure managed tokens for authentication.
  ```bash
  APP_CLIENT_ID=$(az ad app create --display-name "game-manager" --query appId --output tsv)
  spId=$(az ad sp create --id $APP_CLIENT_ID --query id --output tsv)
  cat <<EOF
  Application (Client) ID: $APP_CLIENT_ID
  Service Principal (Object) ID: $spId
  EOF
  ```
- An App Registration role to allow the webhook bearer token to be validated
  ```bash
  cat <<EOF > /tmp/webhook-role.json
  [
    {
      "allowedMemberTypes": ["Application"],
      "description": "Allow Azure to post to webhooks",
      "displayName": "Webhook.Post",
      "id": "$(uuidgen)",
      "isEnabled": true,
      "value": "Webhook.Post"
    }
  ]
  EOF
  az ad app update --id $APP_CLIENT_ID --app-roles @/tmp/webhook-role.json
  ```
- Ownership of the App Registration. This should be the user that runs the azure cli commands. Ownership allows the
  creation of topic subscriptions (i.e. `az eventgrid system-topic event-subscription create`).
  ```bash
  user_id=$(az ad signed-in-user show --query id -o tsv)
  aad_app_id=$(az ad app show --id $APP_CLIENT_ID --query "id" -o tsv)
  az ad app owner add --id $aad_app_id --owner-object-id $user_id
  ```
- For each resource group to be monitored, two system topics must be created and subscribed to.
  ```bash
  #!/bin/bash
  
  RESOURCE_GROUP=<resource-group-name>
  VM_NAME=<vm-name>
  GAME_MANAGER_URL=https://game-manager.example.com
  WEBHOOK_SHARED_SECRET=<query-string-secret> # openssl rand -hex 16
  LOCATION=westus2  
  SUB_ID=$(az account show --query id --output tsv)
  TENANT_ID=$(az account show --query tenantId --output tsv)
  APP_CLIENT_ID=$(az ad app list --display-name "game-manager" --query "[0].appId" -o tsv) # az ad app list --all --query "[].{Name:displayName, ClientID:appId, Domain:publisherDomain}" --output table
  APP_SP_OBJECT_ID=$(az ad sp show --id "$APP_CLIENT_ID" --query id --output tsv)
  WEBHOOK_ROLE_ID=$(az ad app show --id $APP_CLIENT_ID --query "appRoles[?value=='Webhook.Post'].id" -o tsv)
  TOPIC_NAME="$VM_NAME-topic"
  ACTIONS_NAME=$TOPIC_NAME-resources
  HEALTH_NAME=$TOPIC_NAME-healthresources

  function assign_webhook_role() {
    local principalId="$1"
    local resource_sp_object_id="$2"
    local role_id="$3"
    az rest --method POST \
        --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$principalId/appRoleAssignments" \
        --body "{\"principalId\": \"$principalId\", \"resourceId\": \"$resource_sp_object_id\", \"appRoleId\": \"$role_id\"}"
  }
  
  function create_topic() {
      local sub_id="$1"
      local webhook_role_id="$2"
      local resource_sp_object_id="$3"
      local name="$4"
      local type="$5"
      local sp_id
  
      sp_id=$(az eventgrid system-topic create \
          --name "$name" \
          --resource-group "$RESOURCE_GROUP" \
          --topic-type "$type" \
          --source "/subscriptions/$sub_id" \
          --location global \
          --identity systemassigned \
          --query "identity.principalId" -o tsv)
  
      assign_webhook_role "$sp_id" "$resource_sp_object_id" "$webhook_role_id"
  }
  
  function subscribe() {
      local vm_name="$1"
      local tenant_id="$2"
      local gm_app_id="$3"
      local sub_id="$4"
      local topic_name="$5"
      local event_types="$6"
  
      az eventgrid system-topic event-subscription create \
          --name "$topic_name-vm-webhook" \
          --system-topic-name "$topic_name" \
          --resource-group "$RESOURCE_GROUP" \
          --endpoint "$GAME_MANAGER_URL/webhooks/azure/eventgrid?code=$WEBHOOK_SHARED_SECRET" \
          --event-delivery-schema eventgridschema \
          --azure-active-directory-tenant-id "$tenant_id" \
          --azure-active-directory-application-id-or-uri "$gm_app_id" \
          --included-event-types $event_types \
          --subject-begins-with "/subscriptions/$sub_id/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Compute/virtualMachines/$vm_name"
  }
  
  create_topic "$SUB_ID" "$WEBHOOK_ROLE_ID" "$APP_SP_OBJECT_ID" "$ACTIONS_NAME" Microsoft.Resources.Subscriptions
  create_topic "$SUB_ID" "$WEBHOOK_ROLE_ID" "$APP_SP_OBJECT_ID" "$HEALTH_NAME" Microsoft.ResourceNotifications.HealthResources

  subscribe "$VM_NAME" "$TENANT_ID" "$APP_CLIENT_ID" "$SUB_ID" "$ACTIONS_NAME" "Microsoft.Resources.ResourceWriteSuccess Microsoft.Resources.ResourceWriteFailure Microsoft.Resources.ResourceWriteCancel Microsoft.Resources.ResourceDeleteSuccess Microsoft.Resources.ResourceDeleteFailure Microsoft.Resources.ResourceDeleteCancel Microsoft.Resources.ResourceActionSuccess Microsoft.Resources.ResourceActionFailure Microsoft.Resources.ResourceActionCancel"
  subscribe "$VM_NAME" "$TENANT_ID" "$APP_CLIENT_ID" "$SUB_ID" "$HEALTH_NAME" "Microsoft.ResourceNotifications.HealthResources.AvailabilityStatusChanged Microsoft.ResourceNotifications.HealthResources.ResourceAnnotated"
  ```
  
- For each virtual machine that wants to send initialization events, an App Registration role assignment
  ```bash
  gm_sp_object_id=$(az ad sp show --id $APP_CLIENT_ID --query id --output tsv)
  gm_webhook_role_id=$(az ad app show --id $APP_CLIENT_ID --query "appRoles[?value=='Webhook.Post'].id" -o tsv)
  vm_identity_id=$(az vm show --resource-group $RESOURCE_GROUP --name $VM_NAME --query "identity.principalId" -o tsv)
  assign_webhook_role "$vm_identity_id" "$gm_sp_object_id" "$gm_webhook_role_id"
  ```