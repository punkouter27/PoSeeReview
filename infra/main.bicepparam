using './main.bicep'

// Applied by hand (see README.md). Values come from the environment, with local defaults.
param environmentName = readEnvironmentVariable('AZURE_ENV_NAME', 'dev')
param location = readEnvironmentVariable('AZURE_LOCATION', 'westus2')
