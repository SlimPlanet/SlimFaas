import DocumentationPage from '@/components/DocumentationPage';
import { createDocumentationStaticProps } from '@/lib/documentation';

export const getStaticProps = createDocumentationStaticProps('async-functions');

export default DocumentationPage;
