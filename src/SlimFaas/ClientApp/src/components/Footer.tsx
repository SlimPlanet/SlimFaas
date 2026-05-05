import React from 'react';

const Footer: React.FC = () => {
  return (
    <footer className="footer">
      <div className="footer__container">
        <p className="footer__origin">
          SlimFaas was originally created by AXA France.
        </p>
        <div className="footer__cncf">
          <p className="footer__cncf-text">
            We are a Cloud Native Computing Foundation sandbox project.
          </p>
          <img
            src="https://www.cncf.io/wp-content/uploads/2022/07/cncf-stacked-color-bg.svg"
            alt="CNCF Logo"
            className="footer__cncf-logo"
            width={120}
            height={120}
          />
        </div>
        <p className="footer__policy">
          For website terms of use, trademark policy and other project policies please see{' '}
          <a href="https://lfprojects.org/policies/" className="footer__policy-link">
            https://lfprojects.org/policies/
          </a>.
        </p>
        <p className="footer__copyright">
          Copyright SlimFaas, A Series of LF Projects, LLC.
        </p>
      </div>
    </footer>
  );
};

export default Footer;
