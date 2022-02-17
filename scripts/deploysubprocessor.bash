#!/bin/bash
set -euo pipefail
IFS=$'\n\t'

# -e: immediately exit if any command has a non-zero exit status
# -o: prevents errors in a pipeline from being masked
# IFS new value is less likely to cause confusing bugs when looping arrays or arguments (e.g. $@)
#
#FHIR Subscription Processor --- Author Steve Ordahl Principal Architect Health Data Platform
#

# Resources Required by this script 
# Need to add a test to see if these resource providers are enabled
# Service Bus 
# Function App 
# App Insights 


#########################################
# HealthArchitecture Deployment Settings 
#########################################
declare TAG="HealthArchitectures = FHIRSubscription"
declare functionSKU="B1"
declare functionWorkers="2"
declare storageSKU="Standard_LRS"


#########################################
# FHIR Bulk Loader & Export Default App Settings 
#########################################
declare suffix=$RANDOM
declare defresourceGroupLocation="westus2"
declare defresourceGroupName="sub-fhir-"$suffix
declare defdeployPrefix="sub"$suffix
declare defAppName="fsp-"$defdeployPrefix
declare defkeyVaultName="kv-"$defdeployPrefix
declare genPostmanEnv="yes"
declare busnamespaceName="sb-"$defdeployPrefix
declare bustopic="notifyfhirsub"
declare bussub="channelnotify"

#########################################
#  Function Variables 
#########################################
# the import variables and Subscription variables should come from the source code.  The eg endpoints variables are placeholders  
declare importNdjsonvar="ImportNDJSON"
declare importBundle="ImportBundleEventGrid"
declare eventGridEndpointNDJSON=""
declare eventGridEndpointBundle=""
declare egNdjsonSubscription="ndjsoncreated"
declare egBundleSubscription="bundlecreated"

#########################################
#  Common Variables 
#########################################
declare script_dir="$( cd -P -- "$( dirname "${BASH_SOURCE[0]}" )" &> /dev/null && pwd -P )"
declare defSubscriptionId=""
declare subscriptionId=""
declare resourceGroupName=""
declare resourceGroupExists=""
declare useExistingResourceGroup=""
declare createNewResourceGroup=""
declare resourceGroupLocation=""
declare storageAccountNameSuffix="store"
declare storageConnectionString=""
declare serviceplanSuffix="asp"
declare redisAccountNameSuffix="cache"
declare redisConnectionString=""
declare redisKey=""
declare stepresult=""
declare distribution="distribution/publish.zip"
declare postmanTemplate="postmantemplate.json"

# FHIR
declare defAuthType="SP"
declare authType=""
declare fhirServiceUrl=""
declare fhirServiceClientId=""
declare fhirServiceClientSecret=""
declare fhirServiceTenantId=""
declare fhirServiceAudience=""
declare fhirResourceId=""
declare fhirServiceName=""
declare fhirServiceExists=""
declare fhirServiceProperties=""
declare fhirServiceClientAppName=""
declare fhirServiceClientObjectId=""
declare fhirServiceClientRoleAssignment=""

# KeyVault 
declare keyVaultName=""
declare keyVaultExists=""
declare useExistingKeyVault=""
declare createNewKeyVault=""
declare storeFHIRServiceConfig=""
declare storeProxyServiceConfig=""

# Postman 
declare proxyAppName=""
declare fhirServiceUrl=""
declare fhirServiceClientId=""
declare fhirServiceClientSecret=""
declare fhirServiceTenant=""
declare fhirServiceAudience=""

declare fhirProxyEHConnection=""
declare fhirProxyEHName=""
declare fhirProxyEHConsumer="fhirsubprocess"

declare deployPrefix=""
declare stepresult=""
declare option=""
declare defOption="native"
declare defsubscriptionId=""
declare subscriptionId=""
declare resourceGroupName=""
declare resourceGroupLocation=""
declare subAppName=""
declare deployPrefix=""
declare storageConnectionString=""
declare storesourceid=""
declare faresourceid=""
declare stepresult=""
declare keyVaultName=""
declare kvexists=""
declare msi=""
declare fahost=""
declare fsclientid=""
declare fstenantid=""
declare fssecret=""
declare fsresource=""
declare fsurl=""
declare fphost=""
declare fpclientid=""
declare useproxy=""
declare egndjsonresource=""
declare egbundleresource=""
declare createkv=""




#########################################
#  Script Functions 
#########################################

function intro {
	# Display the intro - give the user a chance to cancel 
	#
	echo " "
	echo "FHIR Subscription processor Application installation script... "
	echo " - Prerequisite:  Azure API for FHIR or FHIR Server must be installed"
	echo " - Prerequisite:  Client Application connection information for FHIR Service"
	echo " - Prerequisite:  A Keyvault service"
	echo " - Prerequisite:  FHIR-Proxy EventHub or Event Grid Subscription from Healthcare APIs must be configured"
	echo " "
	echo "Note: You must have rights to able to provision resources within the Subscription scope"
	echo " "
	read -p 'Press Enter to continue, or Ctrl+C to exit'
}


function fail () {
  echo $1 >&2
  exit 1
}


function retry () {
  local n=1
  local max=5
  local delay=30
  while true; do
    "$@" && break || {
      if [[ $n -lt $max ]]; then
        ((n++))
        echo "Command failed. Retry Attempt $n/$max in $delay seconds:" >&2
        sleep $delay ;
      else
        fail "The command has failed after $n attempts."
      fi
    }
  done
}

function kvuri {
	echo "@Microsoft.KeyVault(SecretUri=https://"$keyVaultName".vault.azure.net/secrets/"$@"/)"
}


usage() { echo "Usage: $0 -i <subscriptionId> -g <resourceGroupName> -l <resourceGroupLocation> -n <deployPprefix> -k <keyVaultName> -o <option>" 1>&2; exit 1; }


#########################################
#  Script Main Body (start here) 
#########################################
#
# Initialize parameters specified from command line
#
while getopts ":i:g:l:n:k:o:" arg; do
	case "${arg}" in
		n)
			deployPrefix=${OPTARG:0:14}
			deployPrefix=${deployPrefix,,}
			deployPrefix=${deployPrefix//[^[:alnum:]]/}
			;;
		i)
			subscriptionId=${OPTARG}
			;;
		g)
			resourceGroupName=${OPTARG}
			;;
		l)
			resourceGroupLocation=${OPTARG}
			;;
		k)
			keyVaultName=${OPTARG}
			;;
		o)
			option=${OPTARG}
			;;
		esac
done
shift $((OPTIND-1))
echo "Executing "$0"..."
echo "Checking Azure Authentication..."

#login to azure using your credentials
#
az account show 1> /dev/null

if [ $? != 0 ];
then
	az login
fi

# set default subscription information
#
defsubscriptionId=$(az account show --query "id" --out tsv) 

# Test for correct directory path / destination 
#
if [ -f "${script_dir}/$0" ] && [ -f "${script_dir}/deploysubprocessor.bash" ] ; then
	echo "Checking Script execution directory..."
else
	echo "Please ensure you launch this script from within the ./scripts directory"
	usage ;
fi


# Call the intro function - give the user a chance to exit 
#
intro


# ---------------------------------------------------------------------
# Prompt for common parameters if some required parameters are missing
# 
echo " "
echo "Collecting Azure Parameters (unless supplied on the command line) "

if [[ -z "$subscriptionId" ]]; then
	echo "Enter your subscription ID ["$defsubscriptionId"]:"
	read subscriptionId
	if [ -z "$subscriptionId" ] ; then
		subscriptionId=$defsubscriptionId
	fi
	[[ "${subscriptionId:?}" ]]
fi

if [[ -z "$resourceGroupName" ]]; then
	echo "This script will look for an existing resource group, otherwise a new one will be created "
	echo "You can create new resource groups with the CLI using: az group create "
	echo "Enter a resource group name <press Enter to accept default> ["$defresourceGroupName"]: "
	read resourceGroupName
	if [ -z "$resourceGroupName" ] ; then
		resourceGroupName=$defresourceGroupName
	fi
	[[ "${resourceGroupName:?}" ]]
fi


if [[ -z "$resourceGroupLocation" ]]; then
	echo "If creating a *new* resource group, you need to set a location "
	echo "You can lookup locations with the CLI using: az account list-locations "
	echo "Enter resource group location <press Enter to accept default> ["$defresourceGroupLocation"]: "
	read resourceGroupLocation
	if [ -z "$resourceGroupLocation" ] ; then
		resourceGroupLocation=$defresourceGroupLocation
	fi
	[[ "${resourceGroupLocation:?}" ]]
fi


# Ensure there are subscriptionId and resourcegroup names 
#
if [ -z "$subscriptionId" ] || [ -z "$resourceGroupName" ]; then
	echo "Either one of subscriptionId, resourceGroupName is empty, exiting..."
	exit 1
fi


# Check if the resource group exists
#
echo " "
echo "Checking for existing Resource Group named ["$resourceGroupName"]"
resourceGroupExists=$(az group exists --name $resourceGroupName)
if [[ "$resourceGroupExists" == "true" ]]; then
    echo "  Resource Group ["$resourceGroupName"] found"
    useExistingResourceGroup="yes" 
    createNewResourceGroup="no" ;
else
    echo "  Resource Group ["$resourceGroupName"] not found a new Resource group will be created"
    useExistingResourceGroup="no" 
    createNewResourceGroup="yes"
fi

# ---------------------------------------------------------------------
# Prompt for script parameters if some required parameters are missing
#
echo " "
echo "Collecting Script Parameters (unless supplied on the command line).."

# Set Default Deployment Prefix
#
defdeployPrefix=${defdeployPrefix:0:14}
defdeployPrefix=${defdeployPrefix//[^[:alnum:]]/}
defdeployPrefix=${defdeployPrefix,,}

if [[ -z "$deployPrefix" ]]; then
	echo "Enter your deploy prefix - subscription components begin with this prefix ["$defdeployPrefix"]:"
	read deployPrefix
	if [ -z "$deployPrefix" ] ; then
		deployPrefix=$defdeployPrefix
	fi
	deployPrefix=${deployPrefix:0:14}
	deployPrefix=${deployPrefix//[^[:alnum:]]/}
    deployPrefix=${deployPrefix,,}
	[[ "${deployPrefix:?}" ]]
else 
	subAppName="sub-"${deployPrefix}
fi

# Set a Default Function App Name
# 
if [[ -z "$subAppName" ]]; then
	echo "Enter the FHIR subscription processor app name - this is the name of the function app ["$defAppName"]:"
	read subAppName
	if [ -z "$subAppName" ] ; then
		subAppName=$defAppName
	fi
fi
[[ "${subAppName:?}" ]]

# Set the operation mode 
#
if [[ -z "$option" ]]; then
	echo "Which eventing trigger will you be utilizing Healthcare APIs (native) or FHIR-Proxy (proxy) ["$defOption"]:"
	read option
	if [ -z "$option" ] ; then
		option=$defOption
	fi
fi
[[ "${option:?}" ]]

# Obtain Keyvault Name 
#
if [[ -z "$keyVaultName" ]]; then
	echo "Enter a Key Vault name <press Enter to accept default> ["$defkeyVaultName"]:"
	read keyVaultName
	if [ -z "$keyVaultName" ] ; then
		keyVaultName=$defkeyVaultName
	fi
	[[ "${keyVaultName:?}" ]]
fi

# Check KV exists
#
echo "Checking for keyvault "$keyVaultName"..."
keyVaultExists=$(az keyvault list --query "[?name == '$keyVaultName'].name" --out tsv)
if [[ -n "$keyVaultExists" ]]; then
	set +e 
	echo "  "$keyVaultName" found"
	echo " "
	echo "Checking for FHIR Service configuration..."
	fhirServiceUrl=$(az keyvault secret show --vault-name $keyVaultName --name FS-URL --query "value" --out tsv)
	if [ -n "$fhirServiceUrl" ]; then
		echo "  FHIR Service URL: "$fhirServiceUrl

        fhirResourceId=$(az keyvault secret show --vault-name $keyVaultName --name FS-URL --query "value" --out tsv | awk -F. '{print $1}' | sed -e 's/https\:\/\///g') 
		echo "  FHIR Service Resource ID: "$fhirResourceId 

		fhirServiceTenant=$(az keyvault secret show --vault-name $keyVaultName --name FS-TENANT-NAME --query "value" --out tsv)
		echo "  FHIR Service Tenant ID: "$fhirServiceTenant 
		
		fhirServiceClientId=$(az keyvault secret show --vault-name $keyVaultName --name FS-CLIENT-ID --query "value" --out tsv)
		echo "  FHIR Service Client ID: "$fhirServiceClientId
		
		fhirServiceClientSecret=$(az keyvault secret show --vault-name $keyVaultName --name FS-SECRET --query "value" --out tsv)
		echo "  FHIR Service Client Secret: *****"
		
		fhirServiceAudience=$(az keyvault secret show --vault-name $keyVaultName --name FS-RESOURCE --query "value" --out tsv) 
		echo "  FHIR Service Audience: "$fhirServiceAudience 
		
		if [[ "$option" == "proxy" ]] ; then 
			echo " "
			echo "Checking for FHIR Proxy eventing configuration..."
			fhirProxyEHConnection=$(az keyvault secret show --vault-name $keyVaultName --name FP-MOD-EVENTHUB-CONNECTION --query "value" --out tsv)
			if [ -n "$fhirProxyEHConnection" ] ; then 
				echo "  FHIR Proxy EventHub Connection: ****"
				fhirProxyEHName=$(az keyvault secret show --vault-name $keyVaultName --name FP-MOD-EVENTHUB-NAME --query "value" --out tsv)
				echo "  FHIR Proxy EventHub Name: "$fhirProxyEHName 
				storeProxyServiceConfig="no" ;
			else
				echo "  Unable to read FHIR Proxy Eventing configuration"
				storeProxyServiceConfig="yes"
			fi
		fi
		useExistingKeyVault="yes"
		createNewKeyVault="no"
		storeFHIRServiceConfig="no"	;
	else	
		echo "  unable to read FHIR Service URL from ["$keyVaultName"]" 
        echo "  setting script to create new FHIR Service Entry in existing Key Vault ["$keyVaultName"]"
        useExistingKeyVault="yes"
		storeFHIRServiceConfig="yes"
        createNewKeyVault="no" ;
	fi 
else
	echo "  Script will deploy new Key Vault ["$keyVaultName"]" 
    useExistingKeyVault="no"
    createNewKeyVault="yes"
fi




# Setup type based on input 
# 
if [[ "$createNewKeyVault" == "yes" ]] ; then 
	if [[ "$option" == "proxy" ]] ; then 
		if [ -z "$fhirProxyEHConnection" ] ; then
			echo "Creating a new Key Vault requires manual input of FHIR Proxy Eventing Configuration"
			echo "  Enter the FHIR Proxy Eventing EventHub Connection String:"
			read fhirProxyEHConnection
			if [ -z "$fhirProxyEHConnection" ] ; then
				echo "You must provide a FHIR Proxy Eventing Eventhub Connection String"
				exit 1;
			fi
			[[ "${fhirProxyEHConnection:?}" ]]
		fi

		if [ -z "$fhirProxyEHName" ] ; then
			echo "  Enter the FHIR Proxy Eventing EventHub Name:[fhirevents]"
			read fhirProxyEHName
			if [ -z "$fhirProxyEHName" ] ; then
				fhirProxyEHName="fhirevents"
			fi
			[[ "${fhirProxyEHName:?}" ]]
		fi 
		storeProxyServiceConfig="yes" ;
	fi		
	if [ -z "$fhirServiceUrl" ] ; then
			echo "Creating a new Key Vault requires manual input of FHIR Service Client Information"
			echo "  Enter the FHIR Service URL (aka Endpoint)"
			read fhirServiceUrl
			if [ -z "$fhirServiceUrl" ] ; then
				echo "You must provide a FHIR Service URL"
				exit 1;
			fi
			[[ "${fhirServiceUrl:?}" ]]
	fi 

	if [ -z "$fhirServiceTenant" ] ; then
			echo "  Enter the FHIR Service - Tenant ID (GUID)"
			read fhirServiceTenant
			if [ -z "$fhirServiceTenant" ] ; then
				echo "You must provide a FHIR Service - Tenant ID (GUID)"
				exit 1;
			fi
			[[ "${fhirServiceTenant:?}" ]]
	fi 

	if [ -z "$fhirServiceClientId" ] ; then 
			echo "  Enter the FHIR Service - Client ID (GUID)"
			read fhirServiceClientId
			if [ -z "$fhirServiceClientId" ] ; then
				echo "You must provide a FHIR Service - Client ID (GUID)"
				exit 1;
			fi
			[[ "${fhirServiceClientId:?}" ]]
	fi 

	if [ -z "$fhirServiceClientSecret" ] ; then 
			echo "  Enter the FHIR Service - Client Secret"
			read fhirServiceClientSecret
			if [ -z "$fhirServiceClientSecret" ] ; then
				echo "You must provide a FHIR Service - Client Secret"
				exit 1;
			fi
			[[ "${fhirServiceClientSecret:?}" ]]
	fi 

	if [ -z "$fhirServiceAudience" ] ; then 
			echo "  Enter the FHIR Service - Audience (URL)"
			read fhirServiceAudience
			if [ -z "$fhirServiceAudience" ] ; then
				echo "You must provide a FHIR Service - Audience (URL)"
				exit 1;
			fi
			[[ "${fhirServiceAudience:?}" ]]
	fi 
	storeFHIRServiceConfig="yes"
	
fi

#------------------------------------------------------------------------------
# Prompt for final confirmation
#
echo "--- "
echo "Ready to start deployment of FHIR Subscription Processor Application: ["$subAppName"] with the following values:"
echo "Component Deploy Prefix:............... "$deployPrefix
echo "FHIR Service URL:...................... "$fhirServiceUrl
echo "Eventing Method:..................... "$option
echo "Subscription ID:....................... "$subscriptionId
echo "Resource Group Name:................... "$resourceGroupName
echo " Use Existing Resource Group:.......... "$useExistingResourceGroup
echo " Create New Resource Group:............ "$createNewResourceGroup
echo "Resource Group Location:............... "$resourceGroupLocation 
echo "KeyVault Name:......................... "$keyVaultName
echo " Use Existing Key Vault:............... "$useExistingKeyVault
echo " Create New Key Vault:................. "$createNewKeyVault
echo " "
echo "Please validate the settings above before continuing"
read -p 'Press Enter to continue, or Ctrl+C to exit'


#############################################################
#  Start Setup & Deployment 
#############################################################
#

# set the default subscription id
#
echo " "
echo "Setting default subscription id"
az account set --subscription $subscriptionId


echo "Starting Deployments... "
(
    if [[ "$useExistingResourceGroup" == "no" ]]; then
        echo " "
        echo "Creating Resource Group ["$resourceGroupName"] in location ["$resourceGroupLocation"]"
        set -x
        az group create --name $resourceGroupName --location $resourceGroupLocation --output none --tags $TAG ;
    else
        echo "Using Existing Resource Group ["$resourceGroupName"]"
    fi

    if [[ "$useExistingKeyVault" == "no" ]]; then
        echo " "
        echo "Creating Key Vault ["$keyVaultName"] in location ["$resourceGroupName"]"
        set -x
        stepresult=$(az keyvault create --name $keyVaultName --resource-group $resourceGroupName --location  $resourceGroupLocation --tags $TAG --output none)
		
		sleep 3 ;
    else
        echo "Using Existing Key Vault ["$keyVaultName"]"
    fi
)

echo "Creating Subscription Processor Application"
(

	if [[ "$storeFHIRServiceConfig" == "yes" ]] ; then 
		echo "Storing FHIR Service Values in ["$keyVaultName"]"
		stepresult=$(az keyvault secret set --vault-name $keyVaultName --name "FS-URL" --value $fhirServiceUrl)
		stepresult=$(az keyvault secret set --vault-name $keyVaultName --name "FS-TENANT-NAME" --value $fhirServiceTenant)
		stepresult=$(az keyvault secret set --vault-name $keyVaultName --name "FS-CLIENT-ID" --value $fhirServiceClientId)
		stepresult=$(az keyvault secret set --vault-name $keyVaultName --name "FS-SECRET" --value $fhirServiceClientSecret)
		stepresult=$(az keyvault secret set --vault-name $keyVaultName --name "FS-CLIENT-SECRET" --value $fhirServiceClientSecret)
		stepresult=$(az keyvault secret set --vault-name $keyVaultName --name "FS-RESOURCE" --value $fhirServiceAudience)
	fi 

	if [[ "$storeProxyServiceConfig" == "yes" ]] ; then 
		echo "Storing FHIR Proxy Eventing Configuration Values in ["$keyVaultName"]"
		stepresult=$(az keyvault secret set --vault-name $keyVaultName --name "FP-MOD-EVENTHUB-CONNECTION" --value $fhirProxyEHConnection)
		stepresult=$(az keyvault secret set --vault-name $keyVaultName --name "FP-MOD-EVENTHUB-NAME" --value $fhirProxyEHName)
	fi 

	# Create Storage Account
	#
	echo "Creating Storage Account ["$deployPrefix$storageAccountNameSuffix"]..."
	stepresult=$(az storage account create --name $deployPrefix$storageAccountNameSuffix --resource-group $resourceGroupName --location  $resourceGroupLocation --sku $storageSKU --encryption-services blob --tags $TAG)
	
	# Create Service Bus for Subscription rest-hook notifications
	#
	echo "Creating Service Bus Namespace ["$busnamespaceName"]..."
	stepresult=$(az servicebus namespace create --resource-group $resourceGroupName --name $busnamespaceName --location $resourceGroupLocation)
	#Create Notify Topic
	echo "Creating Topic ["$bustopic"]..."
	stepresult=$(az servicebus topic create --resource-group $resourceGroupName --namespace-name $busnamespaceName --name $bustopic)
	echo "Creating Topic Subscription ["$bussub"]..."
	stepresult=$(az servicebus topic subscription create --resource-group $resourceGroupName --namespace-name $busnamespaceName --topic-name $bustopic --name $bussub)
	echo "Retrieving ServiceBus Connection String and Storing in KeyVault..."
	sbconnectionString=$(az servicebus namespace authorization-rule keys list --resource-group $resourceGroupName --namespace-name $busnamespaceName --name RootManageSharedAccessKey --query primaryConnectionString --output tsv)
	stepresult=$(az keyvault secret set --vault-name $keyVaultName --name "FSP-NOTIFYSB-CONNECTION" --value $sbconnectionString)
	# Create REDIS Cache
	#
	echo "Creating Redis Cache ["$deployPrefix$redisAccountNameSuffix"]..."
	stepresult=$(az redis create --location $resourceGroupLocation --name $deployPrefix$redisAccountNameSuffix --resource-group $resourceGroupName --sku Basic --vm-size c0 --tags $TAG)
	
	echo "Creating Redis Connection String..."
	redisKey=$(az redis list-keys -g $resourceGroupName -n $deployPrefix$redisAccountNameSuffix --query "primaryKey" --out tsv)
	redisConnectionString=$deployPrefix$redisAccountNameSuffix".redis.cache.windows.net:6380,password="$redisKey",ssl=True,abortConnect=False"
	
	echo "Storing Redis Connection String in KeyVault..."
	stepresult=$(az keyvault secret set --vault-name $keyVaultName --name "FSP-REDISCONNECTION" --value $redisConnectionString)
	
	# Create Service Plan
	#
	echo "Creating FHIR Subscription Processor App Serviceplan ["$deployPrefix$serviceplanSuffix"]..."
	stepresult=$(az appservice plan create -g  $resourceGroupName -n $deployPrefix$serviceplanSuffix --number-of-workers $functionWorkers --sku $functionSKU --tags $TAG)
	
	# Create the function app
	echo "Creating FHIR Subscription Processor Function App ["$subAppName"]..."
	fahost=$(az functionapp create --name $subAppName --storage-account $deployPrefix$storageAccountNameSuffix  --plan $deployPrefix$serviceplanSuffix  --resource-group $resourceGroupName --runtime dotnet --os-type Windows --functions-version 3 --query defaultHostName --output tsv)

	echo "FHIR Subscription Processor hostname is: "$fahost
	
	# Setup Auth 
	echo "Creating MSI for FHIR Subscription Processor Function App..."
	msi=$(az functionapp identity assign -g $resourceGroupName -n $subAppName --query "principalId" --out tsv)
	
	# Setup Keyvault Access 
	echo "Setting KeyVault Policy to allow secret access for FHIR Subscription Processor App..."
	stepresult=$(az keyvault set-policy -n $keyVaultName --secret-permissions list get set --object-id $msi)
	
	# Obtain Function Application Key 
	echo "Retrieving FHIR Subscription Processor App Host Key...  note - ths will retry 5 times before failing"
	sleep 3
	faresourceid="/subscriptions/"$subscriptionId"/resourceGroups/"$resourceGroupName"/providers/Microsoft.Web/sites/"$subAppName
	fakey=$(retry az rest --method post --uri "https://management.azure.com"$faresourceid"/host/default/listKeys?api-version=2018-02-01" --query "functionKeys.default" --output tsv)
	
	# Apply App Auth and Connection settings 
	echo "Applying FHIR Subscription Processor App settings ["$subAppName"]..."
	echo " Fhir Service URL will be referenced directly in App Settings for readability"
		stepresult=$(az functionapp config appsettings set --name $subAppName --resource-group $resourceGroupName --settings FS-URL=$fhirServiceUrl FS-TENANT-NAME=$(kvuri FS-TENANT-NAME) FS-CLIENT-ID=$(kvuri FS-CLIENT-ID) FS-SECRET=$(kvuri FS-SECRET) FS-RESOURCE=$(kvuri FS-RESOURCE) FSP-NOTIFYSB-CONNECTION=$(kvuri FSP-NOTIFYSB-CONNECTION) FSP-NOTIFYSB-SUBSCRIPTION=$bussub FSP-NOTIFYSB-TOPIC=$bustopic FSP-REDISCONNECTION=$(kvuri FSP-REDISCONNECTION))
	if [[ "$option" == "proxy" ]]; then
		echo "Adding FHIR Proxy eventing configuration..."
		stepresult=$(az functionapp config appsettings set --name $subAppName --resource-group $resourceGroupName --settings FP-MOD-EVENTHUB-CONNECTION=$(kvuri FP-MOD-EVENTHUB-CONNECTION) FP-MOD-EVENTHUB-NAME=$(kvuri FP-MOD-EVENTHUB-NAME) FSP-CONSUMERGROUPNAME=$fhirProxyEHConsumer)
	else
		echo "Disabling FHIR Proxy Event Hub Trigger will be Using Native EventGrid..."
		stepresult=$(az functionapp config appsettings set --name $subAppName --resource-group $resourceGroupName --settings AzureWebJobs.SubscriptionEventHubProcessor.Disabled=1)
	fi
	
	# Deploy Function Application code
	echo "Deploying FHIR Subscription Processor application repo to ["$subAppName"]...  note - this can take a while"
	stepresult=$(retry az functionapp deployment source config --branch master --manual-integration --name $subAppName --repo-url https://github.com/sordahl-ga/FHIRSubscriptionProcessor --resource-group $resourceGroupName)
	sleep 30	
	#---


	echo " "
	echo "**************************************************************************************"
	echo "FHIR Subscription Processor has successfully been deployed to group "$resourceGroupName" on "$(date)
	echo "Please note the following reference information for future use:"
	echo "Your FHIR Subscription Processor URL is: "$fahost
	echo "Your FHIR Subscription Processor Key is: "$fakey
	echo ""
	echo "Important:"
	if [[ "$option" == "proxy" ]]; then
		echo "Make sure you follow the post install steps for FHIR Proxy Eventing located here:https://github.com/sordahl-ga/FHIRSubscriptionProcessor/scripts/Readme.md"
	else
		echo "Make sure you follow the post install steps for Healthcare APIs native eventing located here:https://github.com/sordahl-ga/FHIRSubscriptionProcessor/scripts/Readme.md"
	fi
	echo "***************************************************************************************"
	echo " "
)
if [ $? != 0 ] ; then
	echo "FHIR Subscription Processor deployment had errors. Consider deleting the resources and trying again..."
fi

