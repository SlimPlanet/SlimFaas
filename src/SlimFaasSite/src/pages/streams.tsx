import DocumentationPage from '@/components/DocumentationPage';
import { createDocumentationStaticProps } from '@/lib/documentation';

export const getStaticProps = createDocumentationStaticProps('streams');
export default DocumentationPage;
