# Governance of the SlimFaas Project

## Objectives

SlimFaas is a lightweight Functions-as-a-Service (FaaS) framework designed for efficient and extensible serverless execution, leveraging open standards and a modular approach.

This document defines the governance model of the SlimFaas project to ensure transparent and collaborative decision-making, aligned with the principles of the **Cloud Native Computing Foundation (CNCF)**.

---

## 1. Governance Model

SlimFaas follows an open governance model based on consensus, with a structure defined in three levels:

- **Contributors**: Anyone submitting code, documentation, or suggestions via issues and pull requests.
- **Maintainers**: A group of individuals with write access to the repository responsible for reviewing and approving contributions.
- **Technical Steering Committee (TSC)**: A set of experienced maintainers responsible for the project's strategic decisions.

---

## 2. Roles and Responsibilities

### 2.1 Contributors
Anyone can contribute to SlimFaas by submitting pull requests (PRs), issues, or participating in discussions. Contributors must adhere to the project's **Code of Conduct**.

**Version Publishing**:
- **Alpha Version**: A contributor can trigger an alpha version by including `(alpha)` in the commit message.
- **Beta Version**: A contributor can trigger a beta version by including `(beta)` in the commit message.

These versions will be generated and published automatically based on the active branch.

### 2.2 Maintainers
Maintainers are contributors who have demonstrated sustained commitment and technical expertise in SlimFaas. Their responsibilities include:
- Reviewing and approving PRs,
- Managing issues and technical discussions,
- Ensuring code quality and stability,
- Assisting new contributors.

**Release Versioning**:
- A **stable release** can only be published from the `main` branch.
- The commit must include the keyword `(release)`.
- Only maintainers can approve and merge a PR into `main`.

### 2.3 Technical Steering Committee (TSC)
The **Technical Steering Committee (TSC)** is responsible for strategic decisions, including:
- The vision and roadmap of the project,
- The integration of new features,
- Critical decisions (licenses, architecture, partnerships).

The TSC consists of at least three members and makes decisions by consensus. If no consensus is reached, a majority vote is conducted.

---

## 3. Automated Semantic Versioning

When a `(release)` commit is merged into `main`, the **tag versioning** is automatically incremented following **Semantic Versioning (SemVer)** rules:

- If the commit message **starts with `fix`**, the **patch version** is incremented (e.g., `1.0.1 → 1.0.2`).
- If the commit message **starts with `feat`**, the **minor version** is incremented (e.g., `1.0.1 → 1.1.0`).
- If the commit message **contains `BREAKING`**, the **major version** is incremented (e.g., `1.0.1 → 2.0.0`).
- If no rule matches, the commit is **considered a fix by default** and increments the patch version.

Additionally, a `CHANGELOG.md` is **automatically generated** from commit messages during a release.

---

## 4. Decision-Making Process

SlimFaas follows a **consensus-based governance model**:
- Discussions occur on **GitHub Issues**, **GitHub Discussions**, and the **community Slack**.
- Operational decisions (PR reviews, issue triage) are made by maintainers.
- Strategic decisions (architecture changes, major new features) are made by the **TSC**.

If a decision remains disputed for a long time, it may be subject to a **formal vote**, where each **TSC** member has one vote.

---

## 5. Security Restrictions on Pull Requests

- **PRs from a fork**:
    - For security reasons, a **pull request from a fork** can only run **unit tests**.
    - Workflows requiring sensitive secrets (e.g., deployment) will not be triggered.

- **PRs from a branch in the main repository**:
    - A PR from a branch in the main repository can execute the full CI/CD pipeline.
    - This includes unit tests, integration tests, and deployment conditioned on the defined rules above.

---

## 6. Membership and Removal

### 6.1 Adding a Maintainer
A contributor may become a maintainer if they meet the following criteria:
- Has actively contributed for several months (code, documentation, discussions),
- Has demonstrated a strong understanding of the project's architecture and principles,
- Is endorsed by at least two current maintainers.

A new maintainer is approved through a **TSC vote**.

### 6.2 Removing a Maintainer
A maintainer may be removed if:
- They have been inactive for more than six months without justification,
- They violate the Code of Conduct,
- They act against the project's interests.

The removal is validated through a **TSC vote** after discussion.

---

## 7. Code of Conduct

SlimFaas follows the **Contributor Covenant Code of Conduct**, ensuring an open and respectful environment for all contributors. Any violation can be reported to maintainers or the TSC.

---

## 8. Licensing and Intellectual Property

SlimFaas is released under the **Apache 2.0 License**, ensuring its openness and accessibility. All contributions must comply with this license.

---

## 9. Communication

Discussions about SlimFaas take place via:
- **GitHub Issues & Discussions**: For bug tracking and feature improvements.
- **Slack** (or another community platform): For real-time discussions.
- **TSC Meetings** (if necessary): For strategic decision-making.

TSC meetings are publicly announced and accessible to all contributors.

---

## 10. Governance Evolution

This document may be updated as needed. Any modification must be approved by the **TSC** and communicated to the community.

---

