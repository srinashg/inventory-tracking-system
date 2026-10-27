# **TechStore — AI Coding Agent Instructions**

10/24/2026

Goal:  
Build an Inventory & Order Management System for an electronics retailer using  
C\#, ASP.NET Core, EF Core, PostgreSQL, xUnit, React, TypeScript. You may also refer to DotNET\_Project\_Blueprint\_Revised.docx to build the system.

Development instructions:

1. Follow the numbered development steps in the specified order.  
2. Complete only one numbered step at a time. Stop and request my approval before advancing.  
3. Do not create additional folders, files, dependencies, or major features beyond the approved step unless necessary. Explain any required exceptions.  
4. Follow Test-Driven Development (Red → Green → Refactor) for every new service behavior.  
5. Before implementing a feature, review its documented requirements, entities, database design, API contract, and service logic.  
6. Keep controllers lightweight and business rules inside services.  
7. Follow `Controller → Service → DbContext → Database`, unless an architectural change is explicitly approved.  
8. Use readable, maintainable, object-oriented C\# code. Favor simple solutions and avoid unnecessary abstractions.  
9. Run relevant tests and report the actual results. Never claim a test passed without executing it.  
10. Explain important implementation decisions, including data structures, algorithms, database operations, and relevant C\#/.NET concepts.

During backend implementation, explain how data flows through the **Controller → Service → DbContext → PostgreSQL → Response** architecture.

For database interactions, explain:

* When and how `DbContext` queries the database.  
* Which SQL operations are performed (`SELECT`, `INSERT`, `UPDATE`, `DELETE`, etc.).  
* When `SaveChangesAsync()` persists changes.  
* When database transactions begin, commit, or roll back.  
* How Entity Framework Core retrieves data and returns results through the service, controller, and API response.

Keep explanations concise, beginner-friendly, and relevant to the feature being implemented.

11. Preserve existing working functionality when making changes.  
12. At the end of each step, summarize what was created, what was tested, what remains incomplete, and what the next step will involve.

## **Documentation**

* `docs/architecture.md`: User requests, expected results, and end-to-end workflows.  
* `docs/entities.md`: Core entities and their responsibilities.  
* `docs/diagrams/schema.dbml`: Database schema and entity relationships.  
* `docs/services.md`: Service responsibilities, step-by-step logic, and testing constraints.

Keep these documents consistent with the implemented application. Do not duplicate information unnecessarily.

## **Development Priority**

Correctness → Readability → Maintainability → Performance.

Optimize when there is a clear technical reason, not simply to make the code more complex.

Steps:

**0\. Project Setup**

This project will use a monolithic architecture and the technology stack specified in the Goal section.

Create only the following four folders:

* `docs`  
* `backend`  
* `frontend`  
* `tests`

Do not create any additional folders or files during this step. Preserve the existing `AGENTS.md` or `CLAUDE.md` instruction file without deleting or recreating it.

Stop after creating the four folders and request approval before proceeding to Step 1\.

1\. In the docs folder, create architecture.md. Give a step by step overview of what the system is supposed to do \- what requests can a user make, and what is the expected result for each request?

**1.5. Define Functional and Nonfunctional Requirements**

In the `docs` folder, create `requirements.md`.

Based on the project goal, `architecture.md`, and the project blueprint, document:

* **Functional requirements:** What the application must do, including product management, inventory tracking, order creation, reservations, fulfillment, and cancellations.  
* **Nonfunctional requirements:** How the application must perform, including correctness, reliability, security, performance, maintainability, and testability.  
* **Acceptance criteria:** Clear, measurable conditions for determining whether each requirement has been satisfied.  
* **Scope boundaries:** Features included in the initial version and features deferred until later.

Ensure that `requirements.md` is consistent with `architecture.md` and the project blueprint.

Do not implement application code during this step.

2\. In the docs folder, create a new diagrams folder. Then in the docs folder, create entities.md. Define all objects/entities that make up the core of the system. Describe each object’s role, and the step they’re involved in. Modify architecture.md to include the appropriate objects in each step.

3\. Now design a database schema consisting of tables for each object, which consist of fields (and their data types), including primary keys and foreign keys, etc. Map out the cardinality between each entity \- 1:1, 1:N, N:N, etc. The schema should account for the following:

* Primary keys, foreign keys, and relationships.  
* Unique SKUs and product–warehouse combinations.  
* Nonnegative inventory and positive order quantities.  
* Appropriate money types, such as PostgreSQL numeric(12,2).  
* Transactions for reservations and shipment updates.  
* Concurrency protection against overselling.

Save the schema in a file called diagrams/schema.dbml.

Use schema.dbml to define tables, columns, data types, keys, relationships, and database constraints. Document transaction boundaries and concurrency strategies in docs/services.md, since these behaviors will be implemented and tested during service development.

**4\. Set up the database foundation using Entity Framework Core**

Create `backend/database` and implement the EF Core entities, `DbContext`, entity relationships, constraints, and initial migrations based on `docs/diagrams/schema.dbml`.

Configure PostgreSQL and verify the application can connect to the database.

Use appropriate data types, primary keys, foreign keys, and uniqueness constraints.

Database operations specific to individual services—including transactions, concurrency handling, and queries—will be implemented and tested during the TDD cycles.

Do not implement business services or controllers during this step.

5\. In the docs folder, create services.md. Define all core distinct services the system will have when a user makes a request. The application uses ASP.NET Core, so it must call an API defined in a controller class (more about that later), so describe the step by step logic for each service appropriately at a high level. Then include testing constraints: boundaries, happy path, exceptions.

For each service, document:

* **Purpose:** What business operation does this service perform?  
* **Request:** What user action triggers the API endpoint?  
* **Service:** What happens step by step, including validation, calculations, and business rules?  
* **Database:** What queries, updates, transactions, constraints, and concurrency protections are involved?  
* **Result:** What data or error is returned to the controller and ultimately the frontend?  
* **Testing constraints:** Happy paths, boundary conditions, invalid inputs, exceptions, and state transitions where applicable.

Use the database requirements defined in Step 3 where relevant to the service.

Before implementing services, create `docs/api-contract.md`.

For each API endpoint, document:

* HTTP method and endpoint path.  
* Expected request data.  
* Expected successful response.  
* Relevant error responses and status codes.  
* Corresponding backend service operation.

Keep API contract documentation consistent with `docs/services.md`.

**6\. Test-Driven Development (TDD) and Service Implementation**

Create `backend/services` and the appropriate test files under `tests`.

Follow the Red → Green → Refactor cycle for each service behavior:

* A. Write an xUnit test based on the requirements and testing constraints in `docs/services.md`.  
* B. Run the test and confirm it fails for the expected behavioral reason.  
* C. Implement the minimum code necessary to pass the test, primarily in the corresponding service class under `backend/services`. Supporting changes may be made in entities or database-access code when necessary.  
* D. Run the test and verify it passes.  
* E. Refactor the implementation while keeping all relevant tests passing.  
* F. Repeat A–E for each service behavior, including happy paths, boundaries, invalid inputs, exceptions, and state transitions.

Use a separate test file for each service. Include integration tests with PostgreSQL when verifying database transactions, concurrency, and persistence behavior.

**7\. Service Implementation Standards**

Throughout Step 6, translate the documented logic in `docs/services.md` into readable, maintainable C\# code.

* Apply OOP and SOLID principles where they improve the design.  
* Use dependency injection and favor composition over unnecessary inheritance.  
* Keep business logic in services rather than controllers.  
* Choose appropriate data structures and algorithms, explaining significant choices.  
* Use brief comments to explain non-obvious logic.  
* Prioritize correctness, readability, and maintainability before optimization.  
* Avoid unnecessary complexity or premature optimization.

After completing the relevant TDD cycles, review the service implementations against these standards. Do not reimplement services that are already working.

**8. Implement REST API Controllers and DTOs**

The entire backend must follow this architecture:

**Controller → Service → DbContext → Database → Response**

Using the API contracts defined in `docs/api-contract.md` during Step 5:

1. Create `backend/controllers` and implement the appropriate ASP.NET Core controller classes.  
2. Create `backend/dto` and define request and response DTOs for each applicable API endpoint.  
3. Connect controllers to their corresponding services using dependency injection.  
4. Keep controllers lightweight. Controllers should receive requests, validate request data, call services, and return appropriate HTTP responses.  
5. Add any necessary supporting files, such as middleware for centralized exception handling.  
6. Write and run API integration tests to verify successful responses, invalid requests, authorization behavior where implemented, and error handling.  
7. Verify that controller responses match the documented API contracts.

Use readable, maintainable, object-oriented code. Avoid duplicating business logic already implemented in services.

Explain the complete request-to-response flow for each implemented API endpoint.

Stop after completing this step and request approval before proceeding.

**9. Develop React + TypeScript frontend**
Build the TechStore employee interface inside frontend/ using React and TypeScript. Use the sample Orders UI in DotNET_Project_Blueprint_Revised.docx as a visual reference, not a pixel-perfect requirement. Preserve the existing backend behavior and API contracts.

A. Initialize the frontend using Vite, React, and TypeScript. Organize application code under `frontend/src/`, including reusable components, page components, a centralized API client, and shared TypeScript types. Follow the Frontend Structure and Testing Standards below.
B. Implement five navigable pages: Dashboard (summary and low-stock alerts); Products (view, create, update, deactivate); Inventory (warehouse stock, receipts, adjustments); Orders (select customer/warehouse/products, create draft, submit, view status, cancel when allowed); Fulfillment (pick, pack, ship).
C. Reuse navigation, layout, forms, tables, loading indicators, and error messages where appropriate. Use typed API request/response models and actual customer/product/warehouse data from the backend; do not hard-code the screenshot's sample values as application state.
D. Connect each supported user action to its matching endpoint in docs/api-contract.md. The frontend sends HTTP requests and displays server responses. Follow: User action in React → ASP.NET Core Controller → Service → EF Core DbContext → PostgreSQL → Service/Controller response → React UI update.
E. Keep business rules, inventory changes, stock validation, and order transitions in the backend. Display clear loading, success, validation, empty, and failure states. Disable duplicate submissions during in-flight requests.
F. Follow the frontend testing standards below. Use Vitest and React Testing Library for unit and component tests, and Playwright for end-to-end browser tests. Apply Red → Green → Refactor to new frontend behaviors where practical. Verify successful interactions, errors, loading states, and the complete order workflow using isolated test data.
G. Confirm that the browser can receive 10 laptops, create an order for 2, submit/reserve, pick, pack, ship, and display 8 available. Also verify that requesting 15 when only 10 are available returns an understandable error and does not change stock. Use a controlled development/test dataset.
H. Update relevant documentation only as needed, report test commands and actual results, explain the React-to-database-to-UI flow, and stop for approval before any later milestone (authentication, Docker Compose, CI/CD or deployment).

**Proposed frontend structure**
frontend/
├── src/
│   ├── components/
│   │   ├── NavBarTitle.tsx
│   │   ├── NavBar.tsx
│   │   ├── PageTitle.tsx
│   │   └── FormField.tsx
│   ├── pages/
│   │   ├── DashboardPage.tsx
│   │   ├── ProductsPage.tsx
│   │   ├── InventoryPage.tsx
│   │   ├── OrdersPage.tsx
│   │   └── FulfillmentPage.tsx
│   ├── api/
│   │   └── client.ts
│   ├── types/
│   │   └── index.ts
│   ├── App.tsx
│   └── main.tsx
├── e2e/
├── package.json
├── tsconfig.json
└── vite.config.ts

**Frontend Structure and Testing Standards**
Use Vite to initialize the React + TypeScript application in frontend/.
Organize application code under frontend/src/, separating reusable components, individual pages, API client functions, and shared TypeScript types.

Implement the following pages:
* Dashboard
* Products
* Inventory
* Orders
* Fulfillment
Use feature-specific form components when appropriate and reuse common UI components without introducing unnecessary abstractions.

**Frontend Testing**
Use the following testing tools:
* **Vitest** as the primary frontend test runner.
* **React Testing Library** for component testing.
* ***@testing-library/user-event*** for simulating user interactions.
* ***jsdom*** as the simulated browser environment.
* ***@testing-library/jest-dom*** for additional DOM assertions.
* **Playwright** for end-to-end browser testing.
Follow Red → Green → Refactor for new frontend behavior where practical.
Test successful interactions, invalid inputs, loading states, API errors, and order status changes.
Use mocked API responses for isolated component tests. Use an isolated test environment and database for end-to-end tests involving the real backend.
Verify that the complete order workflow works through the browser without duplicating backend business logic in React.
Run and report the relevant frontend tests before requesting approval to proceed.

## 10. Implement Authentication and Role-Based Authorization

Secure TechStore as an internal employee application using ASP.NET Core authentication and role-based authorization. Implement login, logout, and role-based access control while preserving the existing backend functionality, frontend workflows, and API contracts where possible.

Follow the security requirements defined in `docs/requirements.md` and the planned role matrix in `docs/api-contract.md`.

**A. Authentication Design**

Before implementing authentication, compare appropriate approaches, including ASP.NET Core Identity and external identity providers.

Recommend the simplest secure solution suitable for this project. Explain the benefits and tradeoffs, including security, maintainability, development complexity, and business needs.

Do not begin implementation until I approve the proposed authentication approach.

**B. Define User Roles and Permissions**

Implement the following roles:

- **Admin:** Full access to the application.
- **Sales:** View products and inventory; create, submit, view, and cancel orders.
- **Warehouse:** View products, inventory, and orders; receive and adjust stock; pick, pack, and ship orders.

All authenticated users may access permitted read-only pages and dashboard information.

Document which endpoints each role can access. Ensure the permission rules are enforced by ASP.NET Core, not merely by hiding buttons in React.

**C. Database and Backend Design**

Implement the necessary user, role, and authentication-related entities.

Use Entity Framework Core migrations for required database schema changes.

Add authentication and authorization configuration to ASP.NET Core.

Keep authentication code separate from inventory and order business logic. Use existing ASP.NET Core security features instead of implementing custom password hashing or token-validation algorithms.

**D. Frontend Authentication**

Implement the necessary React components and pages for:

- Employee login.
- Logout.
- Authentication state management.
- Protected routes.
- Role-aware navigation and actions.
- Clear messages for unauthorized or forbidden requests.

Ensure that users only see actions appropriate to their roles. The backend must independently enforce all permissions.

**E. Security Requirements**

Follow secure authentication practices:

- Store passwords using established password-hashing mechanisms.
- Prefer secure, HttpOnly cookies for browser-based sessions where appropriate.
- Protect against CSRF when using cookie-based authentication.
- Never store passwords, long-lived credentials, or sensitive authentication tokens in localStorage.
- Do not commit secrets or credentials to the repository.
- Configure appropriate cookie, session, and CORS settings.
- Use HTTPS for non-local deployments.
- Ensure anonymous requests to protected endpoints return HTTP 401.
- Ensure authenticated users without sufficient permissions receive HTTP 403.

**F. Test-Driven Development**

Follow Red → Green → Refactor for new authentication and authorization behavior.

Write and run appropriate tests covering:

- Successful login with valid credentials.
- Rejection of invalid credentials.
- Logout and session expiration.
- Anonymous access to protected endpoints (401).
- Forbidden actions for insufficient permissions (403).
- Successful access for authorized roles.
- Protected frontend routes and role-aware UI actions.
- Security-related error handling.

Use xUnit for backend tests, Vitest and React Testing Library for frontend tests, and Playwright for end-to-end authentication workflows.

Use isolated test accounts and a dedicated test database. Do not modify production data during automated testing.

**G. End-to-End Verification**

Verify the following scenarios through the browser:

1. An Admin can access all permitted functionality.
2. A Sales employee can create and submit orders but cannot adjust warehouse stock.
3. A Warehouse employee can receive inventory and fulfill orders but cannot perform restricted Sales actions.
4. An unauthenticated visitor cannot access protected application functionality.
5. Logging out prevents further access to protected resources until the user logs in again.

Also verify that the existing workflow still works for appropriately authorized users:

Receive 10 laptops → Create an order for 2 → Reserve → Pick → Pack → Ship → Confirm 8 available.

**H. Documentation and Completion**

Update relevant documentation, including `docs/architecture.md`, `docs/requirements.md`, `docs/entities.md`, `docs/diagrams/schema.dbml`, `docs/services.md`, and `docs/api-contract.md`, where necessary.

Explain:

- How login works from React through ASP.NET Core.
- How user credentials are verified.
- How authentication differs from authorization.
- How roles control access to API endpoints.
- How authentication-related data is stored and protected.
- Why the chosen security architecture is appropriate for TechStore.

Run the relevant backend, frontend, and end-to-end tests. Report actual results, remaining limitations, and any security concerns.

Preserve existing application functionality.

**Stop after completing Step 10 and request my approval before proceeding to Docker Compose, CI/CD, or deployment.**

## 11. Docker Compose, Local Deployment, and Project Documentation

Containerize the TechStore application using Docker and Docker Compose so the React frontend, ASP.NET Core backend, and PostgreSQL database can run together in a consistent, reproducible local environment.

Create a comprehensive `README.md` explaining how another developer can set up, run, test, and understand the application.

Preserve all existing functionality, authentication, authorization, database behavior, and automated tests. Review `docs/architecture.md`, `docs/requirements.md`, and `docs/deployment-security-checklist.md` before implementation.

**A. Review the Existing Environment**

Before making changes, examine the current project structure, application configuration, database setup, and existing development scripts.

Determine which components should run in separate containers and how they should communicate.

Explain the proposed Docker Compose architecture, including the benefits and tradeoffs of the chosen approach.

Do not begin implementation until I approve the proposed containerization design.

**B. Containerize the Backend**

Create the necessary Dockerfile for the ASP.NET Core API.

- Use a multi-stage Docker build to separate compilation from runtime.
- Use compatible .NET SDK and ASP.NET Core runtime images.
- Configure environment variables for database connectivity and application settings.
- Avoid embedding passwords, connection strings containing secrets, or other credentials in the image.
- Run the application as a non-root user where practical.
- Ensure the API can communicate with PostgreSQL through the Docker Compose network.

**C. Containerize the Frontend**

Create the necessary Dockerfile for the Vite + React + TypeScript application.

- Use a multi-stage Docker build.
- Build the React application into production-ready static files.
- Serve the frontend using an appropriate lightweight web server.
- Configure frontend requests to reach the ASP.NET Core API without exposing internal container addresses to browser code.
- Preserve client-side routing so refreshing any React page works correctly.
- Ensure login, logout, authentication cookies, and CSRF protection continue to function.

**D. Configure Docker Compose**

Create a `compose.yaml` file defining the required services:

- Frontend.
- ASP.NET Core API.
- PostgreSQL.

Configure service networking, startup dependencies, health checks, environment variables, and persistent database storage.

Ensure PostgreSQL data survives container restarts.

Prefer a same-origin browser setup for the frontend and API to simplify cookie authentication and avoid unnecessary CORS configuration.

Use `.env.example` to document required settings without including actual secrets.

Do not introduce additional infrastructure unless technically necessary.

**E. Database Initialization and Security**

Ensure the application starts reliably with its required PostgreSQL schema and authentication tables.

- Define how EF Core migrations are applied.
- Ensure the initial Admin account is configured securely.
- Do not expose database credentials in source control, Docker images, or application logs.
- Use an appropriate persistent and protected location for ASP.NET Core Data Protection keys so authentication sessions survive container restarts.
- Document how developers should provide local secrets.
- Avoid destructive database initialization when containers restart.

Follow the requirements in `docs/deployment-security-checklist.md`.

**F. Testing and Verification**

Verify that Docker Compose can start the complete application successfully.

Test the following scenarios:

1. Start the entire application using Docker Compose.
2. Confirm the frontend loads and the API health endpoint reports a healthy database connection.
3. Log in using a configured employee account.
4. Verify Admin, Sales, and Warehouse permissions remain enforced.
5. Receive 10 laptops, create an order for 2, reserve, pick, pack, ship, and confirm 8 available.
6. Verify insufficient-stock requests are rejected without changing inventory.
7. Restart the containers and verify PostgreSQL data persists.
8. Verify authentication cookies and session handling work after restarting the application, subject to the configured security policies.
9. Confirm that application credentials and sensitive information are not exposed in the repository or container images.

Run the relevant existing xUnit, Vitest, React Testing Library, and Playwright tests.

Add container-specific tests where appropriate. Follow Red → Green → Refactor for new testable behavior.

Report the actual test results.

**G. Create README Documentation**

Create a root-level `README.md` that includes:

- Project overview and business problem.
- Main features and supported employee roles.
- Technology stack.
- High-level architecture: React → ASP.NET Core Controller → Service → DbContext → PostgreSQL → Response.
- Project folder structure.
- Prerequisites.
- Local setup instructions.
- Environment variable and secret configuration.
- Database migration and initial Admin setup.
- Docker Compose commands for starting, stopping, and rebuilding the application.
- How to access the frontend and API.
- How to run backend, frontend, and end-to-end tests.
- Common troubleshooting steps.

Ensure another developer can follow the README to run TechStore without relying on undocumented setup steps.

**H. Documentation and Completion**

Update relevant technical documentation to reflect the final containerized local architecture.

Explain:

- The responsibilities of each Docker container.
- How Docker Compose networking works.
- How the frontend communicates with the backend.
- How the API connects to PostgreSQL.
- How database volumes preserve data.
- How environment variables and secrets are managed.
- How the application initializes and handles container restarts.
- The differences between development and deployment configurations.

Keep the implementation straightforward and avoid unnecessary infrastructure or architectural changes.

Do not implement GitHub Actions CI/CD, Azure deployment, or unrelated features during this step.

At completion, summarize all files created or modified, important design decisions, test commands, actual results, and remaining limitations.

**Stop after completing Step 11 and request my approval before proceeding to Step 12 or any cloud deployment.**

## 12. Git Initialization and GitHub Repository Setup

Establish version control for the completed TechStore application using Git and GitHub.

The goal is to safely publish the project's source code, documentation, Docker configuration, and automated tests while excluding credentials, secrets, generated files, and local development data.

Preserve the existing application functionality and follow the development instructions in `CLAUDE.md`.

**A. Review the Existing Project**

Before making changes, review the existing project structure and determine whether Git has already been initialized.

Review the root `.gitignore`, Docker ignore files, environment configuration, database files, and generated artifacts.

Identify which files should be tracked and which must be excluded.

Present the proposed Git initialization and repository publishing plan.

**Do not initialize Git, create commits, create a GitHub repository, or push anything until I approve the plan.**

**B. Configure Git Ignore Rules**

Update `.gitignore` as necessary to exclude sensitive information and unnecessary generated files.

Ensure the following are appropriately excluded:

- `.env` files containing actual credentials.
- Passwords, API keys, connection strings containing secrets, and local authentication credentials.
- ASP.NET Core user-secrets and Data Protection key files.
- Database dumps, backups, and local database data.
- .NET build artifacts such as `bin/` and `obj/`.
- Frontend dependencies and build output such as `node_modules/` and `dist/`.
- Test results, coverage reports, and Playwright artifacts.
- Operating-system and editor-specific temporary files.

Preserve `.env.example` and other safe configuration templates.

Avoid excluding legitimate application source code, migrations, tests, or documentation.

**C. Review Repository Security**

Before the first commit, inspect all files that Git would track.

Verify that:

- No real credentials, tokens, private keys, or sensitive environment variables are committed.
- Docker Compose configuration contains no hard-coded secrets.
- Database backups and authentication key-ring files are excluded.
- Test fixtures contain no real employee credentials.
- Local-only development configuration is clearly distinguished from production configuration.

Use appropriate secret-scanning tools where available.

Report any findings and explain how they were resolved before proceeding.

Do not display or print secret values in reports or logs.

**D. Initialize Git**

After I approve the plan and the repository security review:

1. Initialize a Git repository at the TechStore project root, if one does not already exist.
2. Use `main` as the default branch.
3. Stage only the intended project files.
4. Review the staged file list and changes before committing.
5. Create an initial commit representing the completed Steps 0–11.

Use a meaningful commit message, such as:

`Initial commit: TechStore full-stack inventory management system`

Do not rewrite existing Git history or delete existing branches.

**E. Create and Configure the GitHub Repository**

Propose a suitable GitHub repository name and description.

Recommend an initial repository visibility setting (public or private) and explain its implications.

Before publishing, confirm that the repository contains no sensitive information.

After I approve the repository details:

- Create the GitHub repository.
- Connect the local Git repository to the GitHub remote.
- Push the `main` branch.
- Verify that the expected files are present.
- Confirm that excluded files and credentials are absent.

Do not publish the repository or change its visibility without my explicit approval.

**F. Verify Version Control**

Verify that:

- `git status` shows a clean working tree after the commit.
- The intended source code, migrations, tests, Dockerfiles, Compose configuration, README, and technical documentation are tracked.
- Secrets and generated artifacts are excluded.
- The GitHub repository contains the expected initial commit.
- A fresh clone contains the files needed to follow the README setup instructions.

Where practical, test the clone without modifying existing development data.

Do not commit actual secrets merely to make the cloned project run.

**G. Update Project Documentation**

Update the README where necessary to explain:

- How to clone the GitHub repository.
- How to configure the environment after cloning.
- Basic Git commands for checking status, creating commits, and pushing changes.
- How to contribute changes safely without committing credentials.
- Where developers can find setup and testing instructions.

Keep documentation concise and consistent with the existing README.

**H. Completion and Approval**

At the end of Step 12:

- Summarize the Git and GitHub configuration.
- Report which files were added or modified.
- Confirm that the repository security review was completed.
- Report the initial commit and GitHub repository URL, if publishing was approved and successful.
- Describe any unresolved issues or limitations.
- Confirm that the application source code and existing functionality were preserved.

Do not implement GitHub Actions, CI/CD workflows, cloud deployment, or unrelated application features during this step.

**Stop after completing Step 12 and request my approval before proceeding to Step 13.**

## 13. GitHub Actions Continuous Integration (CI)

Implement automated continuous integration for TechStore using GitHub Actions.

The goal is to automatically validate code changes before they are merged into `main`, reducing regressions and establishing a reliable development workflow.

CI must validate the existing ASP.NET Core backend, React frontend, PostgreSQL integration, browser-based workflows, and repository security without deploying the application.

Use the existing tests, scripts, project structure, and requirements as the source of truth. Do not duplicate working tests unnecessarily.

**General rules:**

- Follow the existing `CLAUDE.md` approval process.
- Complete only the approved subsection, report the results, and stop for approval before proceeding.
- Use feature branches and pull requests rather than pushing directly to `main`.
- Do not expose secrets in workflows, logs, artifacts, or pull request output.
- Keep CI isolated from the developer's local database and Docker volumes.
- Prefer reproducible, maintainable workflows over unnecessary complexity.
- Do not implement continuous deployment, cloud infrastructure, or unrelated features.

### A. Review Existing Tests and Propose CI Architecture

Before creating workflow files, inspect:

- The `.NET` solution, project files, and existing xUnit suites.
- Frontend scripts in `package.json`.
- Vitest and Playwright configuration.
- Dockerfiles and `compose.yaml`.
- Existing container test scripts.
- `.env.example` and test-specific configuration.
- Repository security settings and GitHub Actions requirements.

Determine:

1. Which checks should execute on every pull request.
2. Which checks should execute on pushes to `main`.
3. Which slower integration or Docker tests should run on a schedule or manually.
4. Required runner environments, dependencies, permissions, and estimated resource usage.
5. How PostgreSQL and temporary test data will be provisioned and cleaned up.
6. How existing tests can run without real credentials or paid services.
7. How required status checks can later be enforced through GitHub branch protection or rulesets.

Propose the workflow structure, job dependencies, triggers, caching strategy, and failure behavior.

**Stop for my approval before creating or modifying any workflow files.**

### B. Backend Build and Unit Tests

Create a GitHub Actions job that:

1. Checks out the repository.
2. Installs the required .NET SDK version.
3. Restores NuGet dependencies.
4. Builds the .NET solution.
5. Executes the existing xUnit service tests.
6. Executes authentication and API tests using their required isolated test environments.
7. Reports failures with useful diagnostic output.
8. Uploads test results where practical and safe.

Use a temporary PostgreSQL service or other isolated database configuration where required.

Never connect CI to the local development database or a production database.

Use appropriate dependency caching to reduce repeated work.

A failing build or test must cause the job to fail.

### C. Frontend Build, Lint and Unit Tests

Create a GitHub Actions job that:

1. Checks out the repository.
2. Installs the appropriate Node.js version.
3. Installs dependencies reproducibly using `npm ci`.
4. Runs the existing frontend lint checks.
5. Runs TypeScript compilation and the production build.
6. Runs the existing Vitest and React Testing Library tests.
7. Reports test failures clearly.

Reuse the current package scripts and lockfile.

Cache npm dependencies where appropriate.

Do not introduce unnecessary frontend frameworks, testing libraries, or build tools.

### D. Integration and End-to-End Browser Tests

Automate the existing Playwright and container integration tests.

Review the current test suites before choosing how to run them:

- Existing Playwright tests.
- `tests/containers/web-image.sh`.
- `tests/containers/compose-stack.sh`.
- `tests/containers/compose-e2e.sh`.
- `tests/containers/backup-restore.sh`.

Implement an appropriate balance between fast feedback and comprehensive verification.

Requirements:

1. Run browser tests against an isolated application instance.
2. Provision PostgreSQL and any required test accounts automatically.
3. Verify the existing Admin, Sales and Warehouse workflows.
4. Verify inventory reservation, fulfillment, and insufficient-stock behavior.
5. Preserve the existing authentication and authorization test coverage.
6. Ensure containers, test databases, and volumes are disposable.
7. Collect useful logs or Playwright traces on failure without exposing credentials, sessions, or other sensitive data.
8. Respect GitHub-hosted runner limits and avoid unnecessary repeated Docker builds.

If some container suites are too expensive for every pull request, propose running them through scheduled or manually triggered workflows.

Do not remove existing test coverage merely to make CI faster.

### E. Automated Security Checks

Add appropriate automated repository security checks.

At minimum, evaluate:

- Gitleaks or an equivalent secret scanner.
- Vulnerability auditing for NuGet dependencies.
- Vulnerability auditing for npm dependencies.
- GitHub Dependabot for supported dependency ecosystems.
- Minimal GitHub Actions token permissions.
- Pinned or otherwise appropriately controlled third-party GitHub Actions.

Security checks must not print credentials or sensitive findings into public logs.

Use supported, maintained tools and document any checks that cannot be implemented reliably.

Do not add hard-coded secrets or production credentials to GitHub Actions.

Explain any vulnerabilities found, including whether they require immediate action or are accepted limitations.

### F. Branch Protection and Pull Request Quality Gates

After the CI workflows pass reliably, review the available GitHub repository rulesets and branch protection options.

Propose protection for `main`, including:

1. Requiring pull requests rather than direct pushes.
2. Requiring successful CI checks before merging.
3. Preventing accidental force pushes and branch deletion.
4. Keeping required status check names stable.
5. Requiring reviews where appropriate for the repository's collaboration model.

Check what GitHub supports for this public repository and its current plan before applying settings.

**Do not enable or change repository protection rules until I explicitly approve the proposed settings.**

Verify that the selected rules do not create an impossible or permanently blocked merge workflow.

### G. Final CI Verification and Documentation

Validate the complete CI process using a dedicated feature branch and pull request.

Verify:

1. A normal pull request triggers the intended workflows.
2. Backend and frontend tests pass when the code is correct.
3. Integration and browser tests execute in isolated environments.
4. A deliberately introduced test failure causes the appropriate workflow to fail.
5. Correcting the failure restores a passing CI result.
6. Security checks operate as intended without leaking sensitive data.
7. The workflows do not require paid cloud hosting or application deployment.
8. The workflows do not alter developer or production databases.

Use safe, temporary test changes for failure verification and remove them before the final merge.

Update `README.md` and relevant technical documentation to explain:

- What CI checks run automatically.
- When each workflow is triggered.
- How to view workflow results.
- How to reproduce relevant checks locally.
- How to troubleshoot common CI failures.
- Which checks are required before a pull request can be merged.

Add a GitHub Actions status badge to the README once the workflow is stable.

### H. Completion and Approval

At the end of Step 13:

- Summarize all GitHub Actions workflows and their responsibilities.
- Report which automated tests and security checks passed.
- Provide links to the relevant workflow runs and pull requests.
- Identify any skipped, deferred, or platform-limited checks.
- Confirm test isolation and secret handling.
- Report the final branch protection configuration, if approved and applied.
- Confirm that existing application functionality was preserved.
- Verify that `main` is clean and synchronized after any approved merges.

**Stop after completing Step 13 and request my approval before beginning Step 14.**

Do not implement CD pipelines, cloud hosting, production deployment, or unrelated application features as part of Step 13.
