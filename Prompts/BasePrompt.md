# Project Variables

| Variable         | Value | Notes                                                    |
| ---------------- | ----- | -------------------------------------------------------- |
| ProjectNamespace | Embranch | Default namespace for the Embranch MCP server |
|                  |       |                                                          |

## C-sharp specific rules
- Please make use of the .net c# 9.0 language
## Software Architecture Rules
- apply boyscout rule - leave all code better than how you found it - but only refactor if on the critical path of your current assignment
- Add Summaries to all classes and class members you create, make sure to make them descriptive. If you update the functioning of a class member update the summary
	- If you create any complex code sections within a member, be sure to add a short explanatory comment in the line above, or behind
- When architecting code structures (classes, structs), prefer to keep processing logic out of the data (apply functional programming whenever possible)
	- create a data struct with just fields, properties and a optionally a constructor and/or destructor
	- create a static utility class (generally named [datastructname]Utility) that contains all processing logic functions as static functions
		- Be mindfull of accessiblity - if tools are only accessed locally mark them private, or internal if they are targeted by a (unit)test
	- If functions require upwards of 4 parameters to function, create a custom data struct to forward data to that function
- Whenever possible - create tests for the functions you create to validate that they work in an atomic fashion. 
	- Use NUnit for unit and integration testing - please place create the tests in a seperate project named {ProjectNamespace}Testing
	- please be descriptive in the test functions' summary on what exactly is tested. If possible, use test fixtures and cases for variant testing
	- Prefer using the Assert.That notation form - for examples on this format see: https://moleseyhill.com/2018-12-01-nunit-assert-that-examples.html
	- When creating tests make sure to do the following:
		- in the teardown, only remove the chroma and dolt collections that were created during the tests; removing the actual files is not possible - they are locked by the python context (and not released until the c# application quits)
# logging Rules
- Store data within the chroma databases' in chunks of maximally 512-1024 tokens

## Task Execution flow
- Before starting work:
	- Validate the Chroma tool targets a storage folder local to the current project folder (we don't want to risk polluting other projects) - if its not, please inform and ask input of the user before continuing 
	- make sure the user defined an 'IssueID variable, 
	- make sure a chroma database named 'ProjectDevelopmentLog' exists - if not create it - we'll refer to this database as 'ProjectDB'
	- make sure a chroma database with the 'IssueID' variable value exists - if not create it - we'll refer to this database as 'IssueDB'
	- if the 'IssueDB' exists, please use the content of that database as additional reference for understanding the problem 
	- If the 'ProjectDB' database exists, scan it for issue(s) database(s) that may be related to the current issue and use these as additional reference for understanding the problem - report to the user which issues you have identified as relevant context
- When starting work:
	- Log a summary of your planned approach and steps in the ‘IssueDB’, mark it as ‘Planned Approach’
	- take heed of the 'Software Architecture Rules' above
- When completing work
	- Make sure to run all relevant unit- and integration tests to validate the updated code is without compile & logic errors